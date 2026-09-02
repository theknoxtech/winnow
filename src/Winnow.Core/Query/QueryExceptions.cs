using System;

namespace Winnow.Core.Query
{
    /// <summary>
    /// The named log does not exist on this machine.
    /// </summary>
    /// <remarks>
    /// Not really an error from the user's point of view, and the caller is expected to swallow it
    /// into an empty result. This is what lets the Domain-Controller-only presets (Directory
    /// Service, DFS Replication, DNS Server) show "0 records" on a workstation instead of an
    /// error dialog, and the same for PrintService on a machine where that log is not enabled.
    /// </remarks>
    public sealed class LogNotFoundException : Exception
    {
        public string LogName { get; }

        public LogNotFoundException(string logName, Exception inner = null)
            : base("Log not found: " + logName, inner)
        {
            LogName = logName;
        }
    }

    /// <summary>The caller lacks rights to read the log - in practice always the Security log.</summary>
    public sealed class LogAccessDeniedException : Exception
    {
        public string LogName { get; }

        public LogAccessDeniedException(string logName, Exception inner = null)
            : base("Access denied reading log: " + logName, inner)
        {
            LogName = logName;
        }
    }

    /// <summary>
    /// Windows rejected the XPath. This is a bug in preset data or in the query builder, never
    /// something the user can fix, so it must surface loudly.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="LogNotFoundException"/> on purpose. The original script could
    /// treat every non-permission failure as "nothing to show" because PowerShell validated the
    /// FilterHashtable before the query ran. Now that the XPath is built here, folding an invalid
    /// query into the not-found path would turn a broken preset into a silent, permanent
    /// "0 records found" - the worst possible failure mode for a diagnostic tool.
    /// </remarks>
    public sealed class InvalidQueryException : Exception
    {
        public string LogName { get; }
        public string XPath { get; }

        public InvalidQueryException(string logName, string xpath, Exception inner = null)
            : base("Invalid query for log '" + logName + "': " + xpath, inner)
        {
            LogName = logName;
            XPath = xpath;
        }
    }
}
