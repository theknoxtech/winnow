using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using Winnow.Core.Presets;
using Winnow.Core.Query;
using Xunit;
using Xunit.Abstractions;

namespace Winnow.Tests
{
    /// <summary>
    /// Tests that talk to the real Windows event log.
    /// </summary>
    /// <remarks>
    /// These exist because the thing most likely to break silently is the XPath itself. A pure
    /// string-comparison test proves the builder produces the string we expected; only Windows can
    /// say whether that string is a query it will actually accept. A malformed query would
    /// otherwise show up as a permanent, unexplained "0 records found".
    /// </remarks>
    public class LiveEventLogTests
    {
        private readonly ITestOutputHelper _output;

        public LiveEventLogTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Asks Windows to parse the query without reading any events from it.</summary>
        private static void AssertQueryParses(string logName, string xpath)
        {
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
                using (new EventLogReader(query)) { }
            }
            catch (EventLogNotFoundException)
            {
                // Log is not present on this machine - says nothing about the XPath.
            }
            catch (UnauthorizedAccessException)
            {
                // Security log without elevation - the query itself parsed fine.
            }
            catch (EventLogException ex) when ((ex.HResult & 0xFFFF) == 15007 || (ex.HResult & 0xFFFF) == 2)
            {
                // Channel not found - again, not an XPath problem.
            }
            catch (EventLogException ex)
            {
                throw new Xunit.Sdk.XunitException(
                    "Windows rejected the query for log '" + logName + "':\n  " + xpath +
                    "\n  " + ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + "): " + ex.Message);
            }
        }

        [Fact]
        public void EveryBuiltInPreset_ProducesAQueryWindowsAccepts()
        {
            foreach (var preset in PresetStore.LoadDefaults().Presets)
            {
                foreach (var clause in preset.Clauses)
                {
                    AssertQueryParses(clause.LogName, EventXPath.Build(clause));
                }
            }
        }

        [Fact]
        public void PresetQueries_WithLevelAndTimeRange_AreAccepted()
        {
            // The manual filter panel can combine a preset's clause with a level and a date range,
            // which produces a longer predicate list than any preset does on its own.
            var start = DateTime.Now.AddDays(-30);
            var end = DateTime.Now;

            foreach (var preset in PresetStore.LoadDefaults().Presets)
            {
                foreach (var clause in preset.Clauses)
                {
                    AssertQueryParses(clause.LogName, EventXPath.Build(clause, 2, start, end));
                }
            }
        }

        [Fact]
        public void ApostropheInProviderName_FallsBackToAQueryWindowsAccepts()
        {
            // The event log's XPath subset has no concat(), so an apostrophe cannot appear in a
            // literal at all. The builder must drop the predicate and defer to a post-filter
            // rather than emit something Windows rejects.
            var clause = new QueryClause
            {
                LogName = "Application",
                ProviderNames = new List<string> { "Bob's Provider" }
            };

            var built = EventXPath.BuildQuery(clause);

            Assert.True(built.NeedsProviderPostFilter);
            AssertQueryParses("Application", built.XPath);
        }

        [Fact]
        public void ApostropheProvider_StillNarrowsResults()
        {
            // The post-filter has to actually be applied, otherwise dropping the predicate would
            // silently widen the preset to the entire log.
            var service = new EventLogService(new WindowsEventLogReader());
            var criteria = new EventQueryCriteria
            {
                Clauses =
                {
                    new QueryClause
                    {
                        LogName = "Application",
                        ProviderNames = new List<string> { "No Such Provider's Name" }
                    }
                },
                MaxEvents = 50
            };

            Assert.Empty(service.Run(criteria).Rows);
        }

        [Fact]
        public void MalformedXPath_IsReportedAsInvalidQueryNotAsMissingLog()
        {
            // The regression this guards: an invalid query being folded into "log not found" and
            // surfacing as a silent empty result.
            var reader = new WindowsEventLogReader();

            Assert.Throws<InvalidQueryException>(() =>
                reader.Read("Application", "*[System[(EventID=", 10, CancellationToken.None).ToList());
        }

        [Fact]
        public void MissingLog_IsReportedAsLogNotFound()
        {
            var reader = new WindowsEventLogReader();

            Assert.Throws<LogNotFoundException>(() =>
                reader.Read("No Such Log " + Guid.NewGuid().ToString("N"), "*", 10, CancellationToken.None).ToList());
        }

        [Fact]
        public void Reader_ReturnsPopulatedRows()
        {
            var reader = new WindowsEventLogReader();
            var rows = reader.Read("Application", "*", 25, CancellationToken.None).ToList();

            if (rows.Count == 0)
            {
                _output.WriteLine("Application log is empty on this machine; skipping.");
                return;
            }

            Assert.All(rows, r =>
            {
                Assert.False(string.IsNullOrWhiteSpace(r.ProviderName));
                Assert.NotEqual(default(DateTime), r.TimeCreated);
                Assert.True(r.RecordId.HasValue);
            });
        }

        [Fact]
        public void ServiceResults_AreSortedNewestFirst()
        {
            // Asserted on the service rather than the reader deliberately. The reader walks the
            // log in reverse record order, and an event's TimeCreated (when it was generated) can
            // differ from the order it was written - two providers writing in the same instant
            // routinely come back interleaved. The service sorts explicitly, exactly as the
            // PowerShell version's Sort-Object TimeCreated -Descending did, and that sorted set is
            // what the grid shows.
            var service = new EventLogService(new WindowsEventLogReader());
            var result = service.Run(new EventQueryCriteria
            {
                Clauses = { new QueryClause { LogName = "Application" } },
                MaxEvents = 200
            });

            if (result.Count < 2)
            {
                _output.WriteLine("Too few events to check ordering; skipping.");
                return;
            }

            Assert.Equal(result.Rows.OrderByDescending(r => r.TimeCreated).Select(r => r.TimeCreated),
                         result.Rows.Select(r => r.TimeCreated));
        }

        [Fact]
        public void MultiClauseResults_AreMergedAndSortedAcrossLogs()
        {
            // The shape the Resource/Memory and DNS Errors presets rely on: two logs merged into
            // one time-ordered list rather than concatenated.
            var service = new EventLogService(new WindowsEventLogReader());
            var result = service.Run(new EventQueryCriteria
            {
                Clauses =
                {
                    new QueryClause { LogName = "Application" },
                    new QueryClause { LogName = "System" }
                },
                MaxEvents = 200
            });

            if (result.Count < 2)
            {
                _output.WriteLine("Too few events to check merge ordering; skipping.");
                return;
            }

            Assert.Equal(result.Rows.OrderByDescending(r => r.TimeCreated).Select(r => r.TimeCreated),
                         result.Rows.Select(r => r.TimeCreated));
            Assert.True(result.Rows.Count <= 200);
        }

        [Fact]
        public void MaxEvents_IsHonoured()
        {
            var reader = new WindowsEventLogReader();
            Assert.True(reader.Read("Application", "*", 5, CancellationToken.None).Count() <= 5);
        }

        [Fact]
        public void Cancellation_StopsTheRead()
        {
            var reader = new WindowsEventLogReader();
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                    reader.Read("Application", "*", 1000, cts.Token).ToList());
            }
        }

        [Fact]
        public void Service_SkipsMissingLogsInsteadOfFailing()
        {
            // The behaviour that lets the Domain-Controller-only presets show 0 rows rather than
            // an error on a workstation.
            var service = new EventLogService(new WindowsEventLogReader());
            var criteria = new EventQueryCriteria
            {
                Clauses =
                {
                    new QueryClause { LogName = "No Such Log " + Guid.NewGuid().ToString("N") },
                    new QueryClause { LogName = "Application" }
                },
                MaxEvents = 10
            };

            var result = service.Run(criteria);

            Assert.Single(result.SkippedLogs);
            _output.WriteLine("Rows from Application: " + result.Count);
        }
    }
}
