using System.Collections.Generic;

namespace EventLogViewer.Core.Query
{
    /// <summary>
    /// The Level dropdown values, matching the original script's $script:LevelMap.
    /// </summary>
    /// <remarks>
    /// Information maps to Level 4 only, deliberately. Windows also logs some informational
    /// events at Level 0 ("LogAlways"), so a Level=4 filter will not return quite everything a
    /// human would call informational - but that is exactly what
    /// Get-WinEvent -FilterHashtable @{Level=4} did, and matching the old behaviour matters more
    /// here than second-guessing it: the acceptance test for this rewrite is that the same
    /// preset returns the same rows as the PowerShell version on the same machine.
    /// </remarks>
    public static class EventLevels
    {
        public const string Any = "Any";

        /// <summary>Ordered for the dropdown; null value means "no Level predicate".</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, int?>> All =
            new List<KeyValuePair<string, int?>>
            {
                new KeyValuePair<string, int?>(Any,           null),
                new KeyValuePair<string, int?>("Critical",    1),
                new KeyValuePair<string, int?>("Error",       2),
                new KeyValuePair<string, int?>("Warning",     3),
                new KeyValuePair<string, int?>("Information", 4),
                new KeyValuePair<string, int?>("Verbose",     5),
            };

        public static int? ValueOf(string name)
        {
            foreach (var kv in All)
                if (string.Equals(kv.Key, name, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }
}
