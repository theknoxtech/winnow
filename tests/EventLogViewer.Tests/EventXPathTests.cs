using System;
using System.Collections.Generic;
using EventLogViewer.Core.Presets;
using EventLogViewer.Core.Query;
using Xunit;

namespace EventLogViewer.Tests
{
    public class EventXPathTests
    {
        private static QueryClause Clause(string log, int[] ids = null, string[] providers = null) =>
            new QueryClause
            {
                LogName = log,
                EventIds = ids != null ? new List<int>(ids) : new List<int>(),
                ProviderNames = providers != null ? new List<string>(providers) : new List<string>()
            };

        [Fact]
        public void WholeLogClause_MatchesEverything()
        {
            // The Domain-Controller-only presets have no event IDs at all.
            Assert.Equal("*", EventXPath.Build(Clause("Directory Service")));
        }

        [Fact]
        public void SingleId_ProducesEqualityPredicate()
        {
            Assert.Equal("*[System[(EventID=1102)]]",
                EventXPath.Build(Clause("Security", new[] { 1102 })));
        }

        [Fact]
        public void MultipleIds_AreOredTogether()
        {
            Assert.Equal("*[System[(EventID=7045 or EventID=7036)]]",
                EventXPath.Build(Clause("System", new[] { 7045, 7036 })));
        }

        [Fact]
        public void DuplicateIds_AreCollapsed()
        {
            Assert.Equal("*[System[(EventID=1000)]]",
                EventXPath.Build(Clause("Application", new[] { 1000, 1000 })));
        }

        [Fact]
        public void ProvidersOnly_ScopeWithoutIds()
        {
            Assert.Equal("*[System[Provider[@Name='disk' or @Name='Microsoft-Windows-Disk']]]",
                EventXPath.Build(Clause("System", null, new[] { "disk", "Microsoft-Windows-Disk" })));
        }

        [Fact]
        public void ProviderAndIds_AreCombinedWithAnd()
        {
            // This is the shape that keeps System-log ID 1 from returning every unrelated source.
            Assert.Equal(
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=1)]]",
                EventXPath.Build(Clause("System", new[] { 1 }, new[] { "Microsoft-Windows-WHEA-Logger" })));
        }

        [Fact]
        public void Level_IsAppended()
        {
            Assert.Equal("*[System[(EventID=1000) and (Level=2)]]",
                EventXPath.Build(Clause("Application", new[] { 1000 }), level: 2));
        }

        [Fact]
        public void LevelAlone_IsValid()
        {
            Assert.Equal("*[System[(Level=3)]]",
                EventXPath.Build(Clause("System"), level: 3));
        }

        [Fact]
        public void TimeRange_IsConvertedToUtc()
        {
            var start = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var end = new DateTime(2024, 3, 2, 12, 0, 0, DateTimeKind.Utc);

            var xpath = EventXPath.Build(Clause("System"), startTime: start, endTime: end);

            Assert.Equal(
                "*[System[TimeCreated[@SystemTime>='2024-03-01T12:00:00.000Z' and " +
                "@SystemTime<='2024-03-02T12:00:00.000Z']]]",
                xpath);
        }

        [Fact]
        public void LocalTime_IsShiftedToUtc()
        {
            // A local-time picker value compared against SystemTime without conversion would shift
            // every bounded search by the machine's UTC offset.
            var local = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Local);
            var expected = local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            Assert.Contains("@SystemTime>='" + expected + "'",
                EventXPath.Build(Clause("System"), startTime: local));
        }

        [Fact]
        public void StartOnly_OmitsUpperBound()
        {
            var xpath = EventXPath.Build(Clause("System"),
                startTime: new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Contains("@SystemTime>=", xpath);
            Assert.DoesNotContain("@SystemTime<=", xpath);
        }

        [Fact]
        public void EverythingTogether_OrdersPredicatesConsistently()
        {
            var xpath = EventXPath.Build(
                Clause("System", new[] { 7031 }, new[] { "Service Control Manager" }),
                level: 2,
                startTime: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(
                "*[System[Provider[@Name='Service Control Manager'] and (EventID=7031) and " +
                "(Level=2) and TimeCreated[@SystemTime>='2024-01-01T00:00:00.000Z']]]",
                xpath);
        }

        [Fact]
        public void BlankProviders_AreIgnored()
        {
            Assert.Equal("*[System[(EventID=1)]]",
                EventXPath.Build(Clause("System", new[] { 1 }, new[] { "", "   ", null })));
        }

        [Theory]
        [InlineData("plain", "'plain'")]
        [InlineData("", "''")]
        public void Literal_QuotesSimpleValues(string input, string expected)
        {
            Assert.Equal(expected, EventXPath.Literal(input));
        }

        [Fact]
        public void Literal_RefusesApostrophes()
        {
            // No escape exists: XPath 1.0 has none inside a literal, and the event log's XPath
            // subset does not provide concat() to work around it.
            Assert.Throws<ArgumentException>(() => EventXPath.Literal("Bob's Provider"));
        }

        [Fact]
        public void ApostropheProvider_IsDeferredToAPostFilter()
        {
            var built = EventXPath.BuildQuery(Clause("Application", null, new[] { "Bob's Provider" }));

            // The predicate is dropped rather than emitted invalid...
            Assert.Equal("*", built.XPath);
            // ...and the caller is told it must narrow the results itself.
            Assert.True(built.NeedsProviderPostFilter);
            Assert.Equal(new[] { "Bob's Provider" }, built.ProviderPostFilter);
        }

        [Fact]
        public void MixedProviders_DeferAllOfThemWhenAnyIsInexpressible()
        {
            // Partially expressing the list would exclude events from the safe providers only if
            // the query kept them - keeping both halves consistent means deferring the whole set.
            var built = EventXPath.BuildQuery(
                Clause("System", new[] { 1 }, new[] { "Microsoft-Windows-WHEA-Logger", "Bob's Provider" }));

            Assert.Equal("*[System[(EventID=1)]]", built.XPath);
            Assert.Equal(new[] { "Microsoft-Windows-WHEA-Logger", "Bob's Provider" }, built.ProviderPostFilter);
        }

        [Fact]
        public void ExpressibleProviders_NeedNoPostFilter()
        {
            var built = EventXPath.BuildQuery(Clause("System", new[] { 1 }, new[] { "Microsoft-Windows-WHEA-Logger" }));

            Assert.False(built.NeedsProviderPostFilter);
            Assert.Empty(built.ProviderPostFilter);
        }

        [Fact]
        public void NullClause_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => EventXPath.Build(null));
        }
    }
}
