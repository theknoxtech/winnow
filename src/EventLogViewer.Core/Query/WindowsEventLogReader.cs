using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading;

namespace EventLogViewer.Core.Query
{
    /// <summary>
    /// The real reader, over System.Diagnostics.Eventing.Reader.
    /// </summary>
    public sealed class WindowsEventLogReader : IEventLogReader
    {
        public IEnumerable<EventRow> Read(string logName, string xpath, int maxEvents,
                                          CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(logName))
                yield break;

            EventLogReader reader;
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    // Newest first, matching Get-WinEvent's default and the old app's
                    // Sort-Object TimeCreated -Descending.
                    ReverseDirection = true
                };
                reader = new EventLogReader(query);
            }
            catch (EventLogNotFoundException ex)
            {
                throw new LogNotFoundException(logName, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new LogAccessDeniedException(logName, ex);
            }
            catch (EventLogException ex)
            {
                // A log name that does not exist at all can surface here rather than as
                // EventLogNotFoundException, so the HRESULT has to be inspected to tell a missing
                // log apart from an XPath Windows refused to parse.
                throw Classify(ex, logName, xpath);
            }

            using (reader)
            {
                var count = 0;
                while (count < maxEvents)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord record;
                    try
                    {
                        record = reader.ReadEvent();
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        throw new LogAccessDeniedException(logName, ex);
                    }
                    catch (EventLogException)
                    {
                        yield break;   // log truncated or rotated mid-read; return what we have
                    }

                    if (record == null)
                        yield break;

                    EventRow row;
                    using (record)
                    {
                        row = ToRow(record, logName);
                    }

                    count++;
                    yield return row;
                }
            }
        }

        /// <summary>
        /// Turns a generic EventLogException into the specific failure it represents, so a
        /// malformed query is never mistaken for an absent log.
        /// </summary>
        /// <remarks>
        /// The exception itself carries nothing reliable to switch on: an invalid query arrives as
        /// HRESULT 0x80131500, the generic managed-exception code, not as ERROR_EVT_INVALID_QUERY,
        /// and its Message is localised. So the discriminator is the log itself - if the channel
        /// exists and the read still failed, the query is what Windows objected to.
        /// </remarks>
        private static Exception Classify(EventLogException ex, string logName, string xpath)
        {
            return LogExists(logName)
                ? (Exception)new InvalidQueryException(logName, xpath, ex)
                : new LogNotFoundException(logName, ex);
        }

        private static bool LogExists(string logName)
        {
            try
            {
                using (new EventLogConfiguration(logName)) { return true; }
            }
            catch (EventLogNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;    // it exists, we just cannot read its configuration
            }
            catch (Exception)
            {
                // Cannot tell. Assume it exists so a genuine query bug is surfaced rather than
                // being hidden as a phantom missing log.
                return true;
            }
        }

        /// <summary>
        /// Snapshots a record into a plain row. The record is disposed by the caller immediately
        /// afterwards, so every field - including the rendered message - must be read here.
        /// </summary>
        private static EventRow ToRow(EventRecord record, string requestedLogName)
        {
            return new EventRow
            {
                TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                Level = SafeLevel(record),
                ProviderName = record.ProviderName,
                Id = record.Id,
                LogName = record.LogName ?? requestedLogName,
                RecordId = record.RecordId,
                Message = SafeMessage(record)
            };
        }

        /// <summary>
        /// FormatDescription throws for events whose provider is uninstalled or whose message
        /// resources are missing - common with third-party apps that have since been removed.
        /// The old app showed an empty message in that case rather than failing the whole search.
        /// </summary>
        private static string SafeMessage(EventRecord record)
        {
            try
            {
                return record.FormatDescription() ?? string.Empty;
            }
            catch (EventLogException)
            {
                return string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeLevel(EventRecord record)
        {
            try
            {
                return record.LevelDisplayName ?? LevelFallback(record.Level);
            }
            catch (EventLogException)
            {
                return LevelFallback(record.Level);
            }
        }

        private static string LevelFallback(byte? level)
        {
            switch (level)
            {
                case 1: return "Critical";
                case 2: return "Error";
                case 3: return "Warning";
                case 4: return "Information";
                case 5: return "Verbose";
                case 0: return "Information";
                default: return string.Empty;
            }
        }

        public IEnumerable<ProviderInfo> FindProviders(string namePattern)
        {
            var session = new EventLogSession();
            List<string> names;
            try
            {
                names = new List<string>(session.GetProviderNames());
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (var name in names)
            {
                if (!Matches(name, namePattern)) continue;

                ProviderInfo info = null;
                try
                {
                    using (var metadata = new ProviderMetadata(name))
                    {
                        info = new ProviderInfo { Name = name };
                        foreach (var link in metadata.LogLinks)
                            info.LogNames.Add(link.LogName);
                    }
                }
                catch (Exception)
                {
                    // Provider registered but its metadata is unreadable (uninstalled, or access
                    // denied for a provider we do not care about) - skip it.
                    info = null;
                }

                if (info != null && info.LogNames.Count > 0)
                    yield return info;
            }
        }

        private static bool Matches(string name, string pattern) =>
            !string.IsNullOrEmpty(name) &&
            !string.IsNullOrEmpty(pattern) &&
            name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
