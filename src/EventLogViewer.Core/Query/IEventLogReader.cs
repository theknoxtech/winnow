using System.Collections.Generic;
using System.Threading;

namespace EventLogViewer.Core.Query
{
    /// <summary>
    /// Reads raw rows out of one log. Abstracted so query composition, filtering, merging and
    /// error mapping can be tested without touching the real Windows event log.
    /// </summary>
    public interface IEventLogReader
    {
        /// <summary>
        /// Streams matching rows, newest first, stopping at <paramref name="maxEvents"/>.
        /// </summary>
        /// <exception cref="LogNotFoundException">The log does not exist on this machine.</exception>
        /// <exception cref="LogAccessDeniedException">The caller lacks rights to read the log.</exception>
        IEnumerable<EventRow> Read(string logName, string xpath, int maxEvents, CancellationToken cancellationToken);

        /// <summary>Provider names matching a wildcard pattern, used by the application search.</summary>
        IEnumerable<ProviderInfo> FindProviders(string namePattern);
    }

    /// <summary>A provider and the logs it writes to.</summary>
    public sealed class ProviderInfo
    {
        public string Name { get; set; }
        public List<string> LogNames { get; set; } = new List<string>();
    }
}
