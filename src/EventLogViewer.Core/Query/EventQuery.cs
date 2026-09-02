using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EventLogViewer.Core.Presets;

namespace EventLogViewer.Core.Query
{
    /// <summary>Everything the UI can ask for in one search.</summary>
    public sealed class EventQueryCriteria
    {
        /// <summary>One or more (log, ids, providers) clauses. Results are merged and re-sorted.</summary>
        public List<QueryClause> Clauses { get; set; } = new List<QueryClause>();

        /// <summary>Windows event level, or null for "Any". See <see cref="EventLevels"/>.</summary>
        public int? Level { get; set; }

        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        /// <summary>Cap on records pulled back, across all clauses.</summary>
        public int MaxEvents { get; set; } = 1000;

        /// <summary>Preset-supplied message substring, applied after the query returns.</summary>
        public string MessageFilter { get; set; }

        /// <summary>User-supplied keyword from the Keyword box, applied after the query returns.</summary>
        public string Keyword { get; set; }

        /// <summary>True when any clause reads the Security log.</summary>
        public bool RequiresElevation =>
            Clauses != null && Clauses.Any(c =>
                string.Equals(c.LogName, "Security", StringComparison.OrdinalIgnoreCase));

        /// <summary>True when results can only be produced by rendering every event's message.
        /// Those searches are the expensive ones and should always show progress.</summary>
        public bool NeedsMessageRendering =>
            !string.IsNullOrEmpty(MessageFilter) || !string.IsNullOrEmpty(Keyword);

        public static EventQueryCriteria FromPreset(PresetDefinition preset, int maxEvents, string keyword)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            return new EventQueryCriteria
            {
                Clauses = preset.Clauses.Select(c => c.Clone()).ToList(),
                MessageFilter = preset.MessageFilter,
                Keyword = keyword,
                MaxEvents = maxEvents
            };
        }
    }

    /// <summary>
    /// Turns a <see cref="QueryClause"/> plus level/time filters into an event log XPath query.
    /// </summary>
    /// <remarks>
    /// This is the same shape of query Get-WinEvent -FilterHashtable builds internally, so
    /// results carry over from the PowerShell version unchanged. Kept free of any
    /// System.Diagnostics.Eventing.Reader dependency so it can be unit tested on its own.
    /// </remarks>
    /// <summary>An XPath query plus anything that could not be expressed inside it.</summary>
    public sealed class BuiltQuery
    {
        public string XPath { get; set; }

        /// <summary>
        /// The full provider list when at least one name could not be embedded in the XPath.
        /// Empty when the query expresses the provider scope on its own.
        /// </summary>
        public List<string> ProviderPostFilter { get; set; } = new List<string>();

        public bool NeedsProviderPostFilter => ProviderPostFilter.Count > 0;
    }

    public static class EventXPath
    {
        /// <summary>Matches every event in the log.</summary>
        public const string MatchAll = "*";

        public static string Build(QueryClause clause, int? level = null,
                                   DateTime? startTime = null, DateTime? endTime = null) =>
            BuildQuery(clause, level, startTime, endTime).XPath;

        public static BuiltQuery BuildQuery(QueryClause clause, int? level = null,
                                            DateTime? startTime = null, DateTime? endTime = null)
        {
            if (clause == null) throw new ArgumentNullException(nameof(clause));

            var built = new BuiltQuery();
            var predicates = new List<string>();

            var providers = Clean(clause.ProviderNames);
            if (providers.Count > 0)
            {
                // The event log implements only a subset of XPath 1.0 - notably without concat(),
                // which is the only way to express an apostrophe inside a string literal. A
                // provider name containing one therefore cannot go into the query at all. Rather
                // than emit a query Windows will reject, drop the provider predicate entirely and
                // let the caller narrow the results afterwards. Real Windows provider names never
                // contain apostrophes, but presets.json is hand-edited and a bad query would
                // surface as an opaque failure.
                var expressible = providers.Where(p => p.IndexOf('\'') < 0).ToList();

                if (expressible.Count == providers.Count)
                {
                    var terms = providers.Select(p => "@Name=" + Literal(p));
                    predicates.Add("Provider[" + string.Join(" or ", terms) + "]");
                }
                else
                {
                    built.ProviderPostFilter = providers;
                }
            }

            var ids = clause.EventIds != null
                ? clause.EventIds.Distinct().ToList()
                : new List<int>();
            if (ids.Count > 0)
            {
                var terms = ids.Select(id => "EventID=" + id.ToString(CultureInfo.InvariantCulture));
                predicates.Add("(" + string.Join(" or ", terms) + ")");
            }

            if (level.HasValue)
                predicates.Add("(Level=" + level.Value.ToString(CultureInfo.InvariantCulture) + ")");

            var timeTerms = new List<string>();
            if (startTime.HasValue)
                timeTerms.Add("@SystemTime>=" + Literal(Utc(startTime.Value)));
            if (endTime.HasValue)
                timeTerms.Add("@SystemTime<=" + Literal(Utc(endTime.Value)));
            if (timeTerms.Count > 0)
                predicates.Add("TimeCreated[" + string.Join(" and ", timeTerms) + "]");

            built.XPath = predicates.Count == 0
                ? MatchAll   // whole-log presets: Directory Service, DFS Replication, DNS Server
                : "*[System[" + string.Join(" and ", predicates) + "]]";

            return built;
        }

        private static List<string> Clean(IEnumerable<string> values) =>
            values == null
                ? new List<string>()
                : values.Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

        /// <summary>
        /// The event log stores SystemTime in UTC, so a local-time picker value has to be
        /// converted before it can be compared. Getting this wrong shifts every time-bounded
        /// search by the machine's UTC offset.
        /// </summary>
        private static string Utc(DateTime value)
        {
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Wraps a value as an XPath string literal.
        /// </summary>
        /// <remarks>
        /// Callers must not pass a value containing an apostrophe. XPath 1.0 has no escape
        /// sequence inside a string literal, and the usual workaround - concat('a', "'", 'b') -
        /// is unavailable because the Windows event log supports only a subset of XPath that
        /// excludes concat(). <see cref="BuildQuery"/> screens such values out beforehand.
        /// </remarks>
        internal static string Literal(string value)
        {
            if (value == null) value = string.Empty;

            if (value.IndexOf('\'') >= 0)
                throw new ArgumentException(
                    "Event log XPath cannot express an apostrophe in a literal: " + value, nameof(value));

            return "'" + value + "'";
        }
    }
}
