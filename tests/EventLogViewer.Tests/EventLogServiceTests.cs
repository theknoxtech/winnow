using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EventLogViewer.Core.Presets;
using EventLogViewer.Core.Query;
using Xunit;

namespace EventLogViewer.Tests
{
    /// <summary>
    /// A scripted reader, so merging, de-duplication, filtering and error handling can be tested
    /// against known data instead of whatever happens to be in this machine's event log.
    /// </summary>
    internal sealed class FakeEventLogReader : IEventLogReader
    {
        private readonly Dictionary<string, List<EventRow>> _logs =
            new Dictionary<string, List<EventRow>>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<string> QueriedXPaths { get; } = new List<string>();
        public List<ProviderInfo> Providers { get; } = new List<ProviderInfo>();

        public FakeEventLogReader WithLog(string name, params EventRow[] rows)
        {
            _logs[name] = rows.ToList();
            return this;
        }

        public FakeEventLogReader WithMissingLog(string name) { _missing.Add(name); return this; }
        public FakeEventLogReader WithDeniedLog(string name) { _denied.Add(name); return this; }

        public IEnumerable<EventRow> Read(string logName, string xpath, int maxEvents, CancellationToken ct)
        {
            QueriedXPaths.Add(xpath);

            if (_missing.Contains(logName)) throw new LogNotFoundException(logName);
            if (_denied.Contains(logName)) throw new LogAccessDeniedException(logName);

            if (!_logs.TryGetValue(logName, out var rows)) yield break;

            var count = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (count++ >= maxEvents) yield break;
                yield return row;
            }
        }

        public IEnumerable<ProviderInfo> FindProviders(string namePattern) =>
            Providers.Where(p => p.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public class EventLogServiceTests
    {
        private static EventRow Row(string log, long id, DateTime when, string message = "", string provider = "P") =>
            new EventRow
            {
                LogName = log,
                RecordId = id,
                TimeCreated = when,
                Message = message,
                ProviderName = provider,
                Id = 1000
            };

        private static readonly DateTime T0 = new DateTime(2024, 1, 1, 12, 0, 0);

        [Fact]
        public void MergesClausesAndSortsNewestFirst()
        {
            var reader = new FakeEventLogReader()
                .WithLog("System", Row("System", 1, T0), Row("System", 2, T0.AddMinutes(-10)))
                .WithLog("Application", Row("Application", 1, T0.AddMinutes(-5)));

            var result = new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses =
                {
                    new QueryClause { LogName = "System" },
                    new QueryClause { LogName = "Application" }
                },
                MaxEvents = 100
            });

            Assert.Equal(new[] { T0, T0.AddMinutes(-5), T0.AddMinutes(-10) },
                         result.Rows.Select(r => r.TimeCreated));
        }

        [Fact]
        public void DeDuplicatesRecordsReachedThroughMoreThanOneClause()
        {
            var duplicate = Row("Application", 7, T0);
            var reader = new FakeEventLogReader().WithLog("Application", duplicate, duplicate);

            var result = new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses = { new QueryClause { LogName = "Application" } },
                MaxEvents = 100
            });

            Assert.Single(result.Rows);
        }

        [Fact]
        public void MissingLogIsRecordedAsSkippedNotThrown()
        {
            // What lets the Domain-Controller-only presets report 0 rows on a workstation.
            var reader = new FakeEventLogReader()
                .WithMissingLog("Directory Service")
                .WithLog("System", Row("System", 1, T0));

            var result = new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses =
                {
                    new QueryClause { LogName = "Directory Service" },
                    new QueryClause { LogName = "System" }
                },
                MaxEvents = 100
            });

            Assert.Equal(new[] { "Directory Service" }, result.SkippedLogs);
            Assert.Single(result.Rows);
        }

        [Fact]
        public void AccessDeniedPropagates()
        {
            // Unlike a missing log, this needs to reach the user as the elevation message.
            var reader = new FakeEventLogReader().WithDeniedLog("Security");

            Assert.Throws<LogAccessDeniedException>(() =>
                new EventLogService(reader).Run(new EventQueryCriteria
                {
                    Clauses = { new QueryClause { LogName = "Security" } },
                    MaxEvents = 10
                }));
        }

        [Fact]
        public void MessageFilterNarrowsResults()
        {
            var reader = new FakeEventLogReader().WithLog("System",
                Row("System", 1, T0, "A kernel driver was installed"),
                Row("System", 2, T0.AddMinutes(-1), "A service was installed"));

            var result = new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses = { new QueryClause { LogName = "System" } },
                MessageFilter = "driver",
                MaxEvents = 100
            });

            Assert.Single(result.Rows);
            Assert.Contains("driver", result.Rows[0].Message);
        }

        [Fact]
        public void MessageFilterIsCaseInsensitive()
        {
            var reader = new FakeEventLogReader().WithLog("System",
                Row("System", 1, T0, "A Kernel DRIVER was installed"));

            var result = new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses = { new QueryClause { LogName = "System" } },
                MessageFilter = "driver",
                MaxEvents = 100
            });

            Assert.Single(result.Rows);
        }

        [Fact]
        public void KeywordAndMessageFilterBothApply()
        {
            // The Keyword box stacks on top of a preset's own filter, as it did before.
            var reader = new FakeEventLogReader().WithLog("System",
                Row("System", 1, T0, "Spooler crashed on PRINTER01"),
                Row("System", 2, T0, "Spooler crashed on PRINTER02"),
                Row("System", 3, T0, "Netlogon crashed on PRINTER01"));

            var result = new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses = { new QueryClause { LogName = "System" } },
                MessageFilter = "Spooler",
                Keyword = "PRINTER01",
                MaxEvents = 100
            });

            Assert.Single(result.Rows);
            Assert.Equal(1, result.Rows[0].RecordId);
        }

        [Fact]
        public void MaxEventsTrimsTheMergedSet()
        {
            var reader = new FakeEventLogReader()
                .WithLog("System", Row("System", 1, T0), Row("System", 2, T0.AddMinutes(-1)))
                .WithLog("Application", Row("Application", 1, T0.AddMinutes(-2)));

            var result = new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses =
                {
                    new QueryClause { LogName = "System" },
                    new QueryClause { LogName = "Application" }
                },
                MaxEvents = 2
            });

            Assert.Equal(2, result.Count);
            Assert.Equal(T0, result.Rows[0].TimeCreated);   // newest kept, not the first read
        }

        [Fact]
        public void CancellationIsReportedNotThrown()
        {
            var reader = new FakeEventLogReader().WithLog("System", Row("System", 1, T0));
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var result = new EventLogService(reader).Run(new EventQueryCriteria
                {
                    Clauses = { new QueryClause { LogName = "System" } },
                    MaxEvents = 100
                }, null, cts.Token);

                Assert.True(result.WasCancelled);
            }
        }

        /// <summary>
        /// Synchronous IProgress. Progress&lt;T&gt; marshals its callback through the synchronization
        /// context - with none installed that means the thread pool, so reports can arrive after
        /// the assertion runs and the test fails intermittently.
        /// </summary>
        private sealed class ImmediateProgress : IProgress<int>
        {
            public List<int> Reports { get; } = new List<int>();
            public void Report(int value) => Reports.Add(value);
        }

        [Fact]
        public void ProgressIsReported()
        {
            var reader = new FakeEventLogReader().WithLog("System",
                Enumerable.Range(1, 600).Select(i => Row("System", i, T0.AddSeconds(-i))).ToArray());

            var progress = new ImmediateProgress();
            new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses = { new QueryClause { LogName = "System" } },
                MaxEvents = 1000
            }, progress);

            // Batched during streaming, then a final total once filtering and sorting are done.
            Assert.NotEmpty(progress.Reports);
            Assert.Equal(600, progress.Reports.Last());
        }

        [Fact]
        public void ProgressReportsTheFinalCountAfterFiltering()
        {
            var reader = new FakeEventLogReader().WithLog("System",
                Row("System", 1, T0, "keep"),
                Row("System", 2, T0, "drop"));

            var progress = new ImmediateProgress();
            new EventLogService(reader).Run(new EventQueryCriteria
            {
                Clauses = { new QueryClause { LogName = "System" } },
                MessageFilter = "keep",
                MaxEvents = 100
            }, progress);

            Assert.Equal(1, progress.Reports.Last());
        }

        [Fact]
        public void SecurityIdentitySearchRequiresAtLeastOneTerm()
        {
            var service = new EventLogService(new FakeEventLogReader());

            Assert.Throws<ArgumentException>(() =>
                service.SearchSecurityIdentity("", "", "", 100, null));
        }

        [Fact]
        public void SecurityIdentitySearchRequiresEverySuppliedTermToMatch()
        {
            var reader = new FakeEventLogReader().WithLog("Security",
                Row("Security", 1, T0, "Account: jdoe  Workstation: WS01  Address: 10.0.0.5"),
                Row("Security", 2, T0, "Account: jdoe  Workstation: WS02  Address: 10.0.0.9"),
                Row("Security", 3, T0, "Account: asmith Workstation: WS01 Address: 10.0.0.5"));

            var result = new EventLogService(reader)
                .SearchSecurityIdentity("jdoe", "WS01", null, 100, null);

            Assert.Single(result.Rows);
            Assert.Equal(1, result.Rows[0].RecordId);
        }

        [Fact]
        public void SecurityIdentitySearchQueriesTheCuratedIdSet()
        {
            var reader = new FakeEventLogReader().WithLog("Security");

            new EventLogService(reader).SearchSecurityIdentity("jdoe", null, null, 100, null);

            var xpath = reader.QueriedXPaths.Single();
            Assert.Contains("EventID=4624", xpath);   // logon
            Assert.Contains("EventID=4740", xpath);   // lockout
            Assert.Contains("EventID=4776", xpath);   // credential validation
        }

        [Fact]
        public void ApplicationSearchQueriesEveryLogItsProvidersWriteTo()
        {
            var reader = new FakeEventLogReader()
                .WithLog("Application", Row("Application", 1, T0, "Chrome crashed", "Chrome"))
                .WithLog("Microsoft-Windows-Chrome/Operational",
                         Row("Microsoft-Windows-Chrome/Operational", 1, T0.AddMinutes(-1), "started", "Chrome"));
            reader.Providers.Add(new ProviderInfo
            {
                Name = "Chrome",
                LogNames = { "Application", "Microsoft-Windows-Chrome/Operational" }
            });

            var result = new EventLogService(reader).SearchApplication("Chrome", 100, null);

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void ApplicationSearchFindsCrashesLoggedUnderTheGenericProvider()
        {
            // An app whose crashes are recorded as "Application Error" rather than under its own
            // provider name is only findable through the message text.
            var reader = new FakeEventLogReader().WithLog("Application",
                Row("Application", 5, T0, "Faulting application name: acme.exe", "Application Error"),
                Row("Application", 6, T0, "Faulting application name: other.exe", "Application Error"));

            var result = new EventLogService(reader).SearchApplication("acme", 100, null);

            Assert.Single(result.Rows);
            Assert.Equal(5, result.Rows[0].RecordId);
        }

        [Fact]
        public void ApplicationSearchDoesNotDoubleCountARecordFoundBothWays()
        {
            var row = Row("Application", 5, T0, "Faulting application name: acme.exe", "Acme");
            var reader = new FakeEventLogReader().WithLog("Application", row);
            reader.Providers.Add(new ProviderInfo { Name = "Acme", LogNames = { "Application" } });

            var result = new EventLogService(reader).SearchApplication("Acme", 100, null);

            Assert.Single(result.Rows);
        }

        [Fact]
        public void ApplicationSearchRequiresAName()
        {
            var service = new EventLogService(new FakeEventLogReader());
            Assert.Throws<ArgumentException>(() => service.SearchApplication("  ", 100, null));
        }

        [Fact]
        public void PresetCriteriaCarryTheMessageFilterAcross()
        {
            var preset = PresetStore.LoadDefaults().Presets.Single(p => p.Id == "printing.spooler-events");

            var criteria = EventQueryCriteria.FromPreset(preset, 500, "kw");

            Assert.Equal("Spooler", criteria.MessageFilter);
            Assert.Equal("kw", criteria.Keyword);
            Assert.Equal(500, criteria.MaxEvents);
            Assert.True(criteria.NeedsMessageRendering);
        }

        [Fact]
        public void PresetCriteriaFlagSecurityPresetsAsNeedingElevation()
        {
            var preset = PresetStore.LoadDefaults().Presets.Single(p => p.Id == "account.logon-events");
            Assert.True(EventQueryCriteria.FromPreset(preset, 100, null).RequiresElevation);
        }

        [Fact]
        public void PresetCriteriaDoNotAliasThePresetsClauses()
        {
            // The criteria must be a copy - mutating a search must never edit the stored preset.
            var preset = PresetStore.LoadDefaults().Presets.Single(p => p.Id == "hardware.whea");
            var criteria = EventQueryCriteria.FromPreset(preset, 100, null);

            criteria.Clauses[0].EventIds.Add(999);

            Assert.DoesNotContain(999, preset.Clauses[0].EventIds);
        }
    }
}
