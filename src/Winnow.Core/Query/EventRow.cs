using System;

namespace Winnow.Core.Query
{
    /// <summary>
    /// One result row. A plain snapshot - it holds no native event log handle.
    /// </summary>
    /// <remarks>
    /// The message is rendered while streaming rather than lazily on demand. Lazy rendering would
    /// mean keeping an EventRecord (and with it an open EVT_HANDLE) alive for every row in the
    /// grid; at the 50,000-event ceiling that is tens of thousands of live handles held for as
    /// long as the user leaves the results on screen. It also would not help the cases that
    /// actually dominate here - keyword search, the messageFilter presets, sorting by message and
    /// CSV export all force every message to be rendered anyway. Rendering eagerly on a
    /// background thread, in batches, with cancellation, gets the responsiveness without the
    /// handle-lifetime hazard.
    /// </remarks>
    public sealed class EventRow
    {
        public DateTime TimeCreated { get; set; }
        public string Level { get; set; }
        public string ProviderName { get; set; }
        public int Id { get; set; }
        public string LogName { get; set; }
        public long? RecordId { get; set; }
        public string Message { get; set; }

        /// <summary>Identity used to de-duplicate across clauses that overlap
        /// (the application search can reach the same record through two providers).</summary>
        public string DedupeKey => LogName + "|" + (RecordId?.ToString() ?? Guid.NewGuid().ToString());

        public bool MessageContains(string term) =>
            !string.IsNullOrEmpty(Message) &&
            Message.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
