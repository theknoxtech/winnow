using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventLogViewer.Core.Presets;

namespace EventLogViewer.Core.Query
{
    /// <summary>Outcome of a search: the rows, plus anything the user should know about.</summary>
    public sealed class QueryResult
    {
        public List<EventRow> Rows { get; set; } = new List<EventRow>();

        /// <summary>Logs that did not exist on this machine. Not errors - a note for the status
        /// bar, so "0 records" on a workstation is explainable rather than mysterious.</summary>
        public List<string> SkippedLogs { get; set; } = new List<string>();

        public bool WasCancelled { get; set; }
        public int Count => Rows.Count;
    }

    /// <summary>
    /// Runs searches against the event log: presets, manual filters, application search and
    /// security-identity search.
    /// </summary>
    /// <remarks>
    /// Everything here is async and cancellable. The PowerShell version ran queries synchronously
    /// on the UI thread with Application.DoEvents() to keep painting, because a PowerShell
    /// scriptblock cannot run as a .NET delegate on a thread with no runspace attached. In C#
    /// that constraint simply does not exist, so a 50,000-event query can now run off the UI
    /// thread and be stopped part-way.
    /// </remarks>
    public sealed class EventLogService
    {
        /// <summary>
        /// Identity-relevant Security event IDs, from the original script's
        /// $script:SecurityIdentityIds: logon/logoff, explicit-credential and special-privilege
        /// logons, account management, group membership, lockouts and Kerberos.
        /// </summary>
        public static readonly int[] SecurityIdentityIds =
        {
            4624, 4625, 4634, 4647, 4648, 4672,
            4720, 4722, 4725, 4726, 4738,
            4728, 4729, 4732, 4733, 4756, 4757,
            4740, 4768, 4769, 4771, 4776
        };

        /// <summary>Generic Application-log crash/hang IDs, used as a fallback in the application
        /// search for apps whose crashes are logged under "Application Error" rather than their
        /// own provider name.</summary>
        private static readonly int[] GenericCrashIds = { 1000, 1001, 1002 };

        /// <summary>How many rows to accumulate before reporting progress.</summary>
        private const int ProgressBatchSize = 250;

        private readonly IEventLogReader _reader;

        public EventLogService(IEventLogReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public static EventLogService CreateDefault() => new EventLogService(new WindowsEventLogReader());

        public Task<QueryResult> RunAsync(EventQueryCriteria criteria,
                                          IProgress<int> progress = null,
                                          CancellationToken cancellationToken = default(CancellationToken))
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            return Task.Run(() => Run(criteria, progress, cancellationToken), cancellationToken);
        }

        /// <summary>Synchronous core, so tests do not have to deal with scheduling.</summary>
        public QueryResult Run(EventQueryCriteria criteria,
                               IProgress<int> progress = null,
                               CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new QueryResult();
            var rows = new List<EventRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var clause in criteria.Clauses ?? new List<QueryClause>())
                {
                    if (clause == null || string.IsNullOrWhiteSpace(clause.LogName)) continue;

                    var built = EventXPath.BuildQuery(clause, criteria.Level, criteria.StartTime, criteria.EndTime);

                    try
                    {
                        // Each clause gets the full MaxEvents budget, matching the old app, which
                        // passed -MaxEvents to each Get-WinEvent call separately. The merged set
                        // is trimmed to MaxEvents at the end.
                        foreach (var row in _reader.Read(clause.LogName, built.XPath, criteria.MaxEvents, cancellationToken))
                        {
                            // A provider name the XPath subset could not express is applied here
                            // instead, so the clause still means what the preset says it means.
                            if (built.NeedsProviderPostFilter && !MatchesProvider(row, built.ProviderPostFilter))
                                continue;

                            if (seen.Add(row.DedupeKey))
                            {
                                rows.Add(row);
                                if (progress != null && rows.Count % ProgressBatchSize == 0)
                                    progress.Report(rows.Count);
                            }
                        }
                    }
                    catch (LogNotFoundException)
                    {
                        // Expected on machines without this log - a DC-only log on a workstation,
                        // or PrintService where operational logging is off.
                        result.SkippedLogs.Add(clause.LogName);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.WasCancelled = true;
            }

            result.Rows = Finish(rows, criteria);
            progress?.Report(result.Rows.Count);
            return result;
        }

        private static bool MatchesProvider(EventRow row, List<string> providers) =>
            providers.Any(p => string.Equals(p, row.ProviderName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Applies post-query message filters, sorts newest first and trims to MaxEvents.</summary>
        private static List<EventRow> Finish(IEnumerable<EventRow> rows, EventQueryCriteria criteria)
        {
            IEnumerable<EventRow> q = rows;

            if (!string.IsNullOrEmpty(criteria.MessageFilter))
                q = q.Where(r => r.MessageContains(criteria.MessageFilter));

            if (!string.IsNullOrEmpty(criteria.Keyword))
                q = q.Where(r => r.MessageContains(criteria.Keyword));

            return q.OrderByDescending(r => r.TimeCreated)
                    .Take(Math.Max(0, criteria.MaxEvents))
                    .ToList();
        }

        /// <summary>
        /// Application search: finds every provider whose name contains the term, queries every
        /// log those providers write to, then also checks the Application log's generic crash/hang
        /// IDs for messages mentioning the term.
        /// </summary>
        public Task<QueryResult> SearchApplicationAsync(string appName, int maxEvents, string keyword,
                                                        IProgress<int> progress = null,
                                                        CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(() => SearchApplication(appName, maxEvents, keyword, progress, cancellationToken),
                            cancellationToken);
        }

        public QueryResult SearchApplication(string appName, int maxEvents, string keyword,
                                             IProgress<int> progress = null,
                                             CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(appName))
                throw new ArgumentException("An application name is required.", nameof(appName));

            var clauses = new List<QueryClause>();

            // Group matching providers by the log they write to, so each log is queried once with
            // all of its relevant providers rather than once per provider.
            var byLog = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _reader.FindProviders(appName))
            {
                foreach (var log in provider.LogNames)
                {
                    if (string.IsNullOrWhiteSpace(log)) continue;
                    if (!byLog.TryGetValue(log, out var list))
                    {
                        list = new List<string>();
                        byLog[log] = list;
                    }
                    if (!list.Contains(provider.Name, StringComparer.OrdinalIgnoreCase))
                        list.Add(provider.Name);
                }
            }

            foreach (var kv in byLog)
                clauses.Add(new QueryClause { LogName = kv.Key, ProviderNames = kv.Value });

            var criteria = new EventQueryCriteria
            {
                Clauses = clauses,
                MaxEvents = maxEvents,
                Keyword = keyword
            };
            var result = Run(criteria, progress, cancellationToken);

            // Fallback pass: crashes logged under the generic "Application Error" provider carry
            // the app name only in the message text.
            if (!result.WasCancelled)
            {
                var fallback = new EventQueryCriteria
                {
                    Clauses = new List<QueryClause>
                    {
                        new QueryClause { LogName = "Application", EventIds = GenericCrashIds.ToList() }
                    },
                    MaxEvents = maxEvents,
                    MessageFilter = appName,
                    Keyword = keyword
                };
                var extra = Run(fallback, null, cancellationToken);

                var seen = new HashSet<string>(result.Rows.Select(r => r.DedupeKey), StringComparer.OrdinalIgnoreCase);
                foreach (var row in extra.Rows)
                    if (seen.Add(row.DedupeKey))
                        result.Rows.Add(row);

                result.Rows = result.Rows
                    .OrderByDescending(r => r.TimeCreated)
                    .Take(Math.Max(0, maxEvents))
                    .ToList();
            }

            return result;
        }

        /// <summary>
        /// Security-identity search: pulls the curated identity event set and keeps rows whose
        /// message matches every field the user filled in.
        /// </summary>
        public Task<QueryResult> SearchSecurityIdentityAsync(string userName, string hostName, string ipAddress,
                                                             int maxEvents, string keyword,
                                                             IProgress<int> progress = null,
                                                             CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(() => SearchSecurityIdentity(userName, hostName, ipAddress, maxEvents, keyword,
                                                         progress, cancellationToken),
                            cancellationToken);
        }

        public QueryResult SearchSecurityIdentity(string userName, string hostName, string ipAddress,
                                                  int maxEvents, string keyword,
                                                  IProgress<int> progress = null,
                                                  CancellationToken cancellationToken = default(CancellationToken))
        {
            var terms = new[] { userName, hostName, ipAddress }
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .ToList();

            if (terms.Count == 0)
                throw new ArgumentException("Enter at least one of User, Host, or IP.");

            var criteria = new EventQueryCriteria
            {
                Clauses = new List<QueryClause>
                {
                    new QueryClause { LogName = "Security", EventIds = SecurityIdentityIds.ToList() }
                },
                MaxEvents = maxEvents,
                Keyword = keyword
            };

            var result = Run(criteria, progress, cancellationToken);

            // Every supplied field must match, as in the original Where-Object chain.
            result.Rows = result.Rows
                .Where(r => terms.All(r.MessageContains))
                .ToList();

            return result;
        }
    }
}
