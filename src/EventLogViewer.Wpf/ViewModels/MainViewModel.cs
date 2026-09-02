using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using EventLogViewer.Core.Export;
using EventLogViewer.Core.Hosting;
using EventLogViewer.Core.Presets;
using EventLogViewer.Core.Query;
using EventLogViewer.Core.Update;

namespace EventLogViewer.Wpf.ViewModels
{
    public sealed class MainViewModel : ViewModelBase
    {
        /// <summary>Log names offered in the dropdown. The box stays editable, so any log can be typed.</summary>
        public static readonly string[] DefaultLogSources =
        {
            "Application",
            "System",
            "Security",
            "Setup",
            "Windows PowerShell",
            "Microsoft-Windows-PrintService/Operational",
            "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational",
            "Microsoft-Windows-PowerShell/Operational",
            "Microsoft-Windows-Windows Defender/Operational",
            "Directory Service",
            "DFS Replication",
            "DNS Server",
            "Microsoft-Windows-Kernel-PnP/Configuration"
        };

        private readonly EventLogService _service;
        private readonly IUserInteraction _ui;

        private CancellationTokenSource _cts;

        public MainViewModel(EventLogService service, PresetStore presets, HostEnvironment host, IUserInteraction ui)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            Store = presets ?? throw new ArgumentNullException(nameof(presets));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));

            LogSources = new ObservableCollection<string>(DefaultLogSources);
            SelectedLogSource = LogSources[0];

            Levels = new ObservableCollection<string>(EventLevels.All.Select(l => l.Key));
            SelectedLevel = Levels[0];

            Presets = new ObservableCollection<PresetDefinition>(Store.Presets);

            Rows = new ObservableCollection<EventRow>();
            RowsView = CollectionViewSource.GetDefaultView(Rows);
            RowsView.Filter = LiveFilterPredicate;

            PresetWarning = Store.LoadWarning;
            EnvironmentText = Host.Describe();

            SearchCommand = new RelayCommand(async () => await SearchAsync(), () => !IsSearching);
            AppSearchCommand = new RelayCommand(async () => await AppSearchAsync(), () => !IsSearching);
            SecuritySearchCommand = new RelayCommand(async () => await SecuritySearchAsync(), () => !IsSearching);
            PresetCommand = new RelayCommand(async p => await PresetSearchAsync(p as PresetDefinition), _ => !IsSearching);
            CancelCommand = new RelayCommand(Cancel, () => IsSearching);
            ClearCommand = new RelayCommand(Clear, () => !IsSearching);
            ExportCommand = new RelayCommand(Export, () => Rows.Count > 0 && !IsSearching);
            EditPresetsCommand = new RelayCommand(EditPresets, () => !IsSearching);
            UpdateActionCommand = new RelayCommand(RunUpdateAction, () => HasUpdate);
        }

        public PresetStore Store { get; }
        public HostEnvironment Host { get; }

        public ObservableCollection<string> LogSources { get; }
        public ObservableCollection<string> Levels { get; }
        public ObservableCollection<PresetDefinition> Presets { get; }
        public ObservableCollection<EventRow> Rows { get; }
        public ICollectionView RowsView { get; }

        public RelayCommand SearchCommand { get; }
        public RelayCommand AppSearchCommand { get; }
        public RelayCommand SecuritySearchCommand { get; }
        public RelayCommand PresetCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ClearCommand { get; }
        public RelayCommand ExportCommand { get; }
        public RelayCommand EditPresetsCommand { get; }
        public RelayCommand UpdateActionCommand { get; }

        #region Filter inputs

        private string _selectedLogSource;
        public string SelectedLogSource { get => _selectedLogSource; set => Set(ref _selectedLogSource, value); }

        private string _selectedLevel;
        public string SelectedLevel { get => _selectedLevel; set => Set(ref _selectedLevel, value); }

        private string _eventIdText = "";
        public string EventIdText { get => _eventIdText; set => Set(ref _eventIdText, value); }

        private int _maxEvents = 1000;
        public int MaxEvents { get => _maxEvents; set => Set(ref _maxEvents, value); }

        private string _keyword = "";
        public string Keyword { get => _keyword; set => Set(ref _keyword, value); }

        private bool _useFrom;
        public bool UseFrom { get => _useFrom; set => Set(ref _useFrom, value); }

        private DateTime _fromDate = DateTime.Now.AddDays(-7);
        public DateTime FromDate { get => _fromDate; set => Set(ref _fromDate, value); }

        private bool _useTo;
        public bool UseTo { get => _useTo; set => Set(ref _useTo, value); }

        private DateTime _toDate = DateTime.Now;
        public DateTime ToDate { get => _toDate; set => Set(ref _toDate, value); }

        private string _appName = "";
        public string AppName { get => _appName; set => Set(ref _appName, value); }

        private string _secUser = "";
        public string SecUser { get => _secUser; set => Set(ref _secUser, value); }

        private string _secHost = "";
        public string SecHost { get => _secHost; set => Set(ref _secHost, value); }

        private string _secIp = "";
        public string SecIp { get => _secIp; set => Set(ref _secIp, value); }

        #endregion

        #region Results and status

        private EventRow _selectedRow;
        public EventRow SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (Set(ref _selectedRow, value)) Raise(nameof(DetailText));
            }
        }

        /// <summary>Full message of the selected row, shown in the detail pane.</summary>
        public string DetailText => _selectedRow?.Message ?? string.Empty;

        private string _liveFilter = "";
        public string LiveFilter
        {
            get => _liveFilter;
            set
            {
                if (!Set(ref _liveFilter, value)) return;
                RowsView.Refresh();
                Raise(nameof(LiveCountText));
            }
        }

        /// <summary>
        /// Live filter across message, source and event id.
        /// </summary>
        /// <remarks>
        /// A predicate rather than the DataTable.DefaultView.RowFilter string the WinForms version
        /// built. That string had to have single quotes escaped by hand to avoid a malformed
        /// filter expression; a predicate has no such syntax to get wrong.
        /// </remarks>
        private bool LiveFilterPredicate(object item)
        {
            var term = _liveFilter?.Trim();
            if (string.IsNullOrEmpty(term)) return true;
            if (!(item is EventRow row)) return false;

            return Contains(row.Message, term)
                || Contains(row.ProviderName, term)
                || Contains(row.Id.ToString(CultureInfo.InvariantCulture), term);
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        public string LiveCountText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_liveFilter)) return "";
                return RowsView.Cast<object>().Count() + " shown";
            }
        }

        private string _statusText = "Ready";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private string _countText = "";
        public string CountText { get => _countText; set => Set(ref _countText, value); }

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            set
            {
                if (Set(ref _isSearching, value)) RelayCommand.RaiseCanExecuteChanged();
            }
        }

        private string _presetWarning;
        public string PresetWarning { get => _presetWarning; set => Set(ref _presetWarning, value); }

        private string _environmentText;
        public string EnvironmentText { get => _environmentText; set => Set(ref _environmentText, value); }

        #endregion

        #region Update notification

        private UpdateInfo _update;

        private bool _hasUpdate;
        public bool HasUpdate
        {
            get => _hasUpdate;
            private set
            {
                if (Set(ref _hasUpdate, value)) RelayCommand.RaiseCanExecuteChanged();
            }
        }

        private string _updateText = "";
        public string UpdateText { get => _updateText; private set => Set(ref _updateText, value); }

        /// <summary>
        /// Starts the notify-only update check. Fire-and-forget on purpose: it must never delay
        /// the window appearing, and a failure is never surfaced.
        /// </summary>
        public async Task CheckForUpdateAsync()
        {
            try
            {
                var info = await new UpdateChecker().CheckAsync(UpdateChecker.CurrentVersion())
                                                    .ConfigureAwait(true);
                if (info == null) return;

                _update = info;
                // Launching a browser as SYSTEM on an alternate desktop either silently does
                // nothing or starts a browser nobody can see, so offer the link instead.
                UpdateText = Host.IsBackstageLikely
                    ? "Update available: " + info.TagName + " (click to copy link)"
                    : "Update available: " + info.TagName + " (click to download)";
                HasUpdate = true;
            }
            catch
            {
                // Offline, blocked, or rate limited - all normal, none worth a dialog.
            }
        }

        private void RunUpdateAction()
        {
            if (_update?.ReleaseUrl == null) return;

            if (Host.IsBackstageLikely)
            {
                _ui.CopyToClipboard(_update.ReleaseUrl);
                _ui.ShowInfo("Update available",
                    "Release page:\n\n" + _update.ReleaseUrl +
                    "\n\nThe link has been copied to the clipboard on this machine.");
            }
            else
            {
                _ui.OpenUrl(_update.ReleaseUrl);
            }
        }

        #endregion

        #region Searches

        private async Task SearchAsync()
        {
            var logName = (SelectedLogSource ?? "").Trim();
            if (string.IsNullOrEmpty(logName))
            {
                _ui.ShowError("Validation", "Please select or enter a Log Source.");
                return;
            }

            if (!ConfirmElevation(logName)) return;

            var clause = new QueryClause { LogName = logName };

            if (!string.IsNullOrWhiteSpace(EventIdText))
            {
                if (!TryParseIds(EventIdText, out var ids))
                {
                    _ui.ShowError("Validation",
                        "Invalid Event ID: '" + EventIdText + "'\nUse comma-separated integers.");
                    return;
                }
                clause.EventIds = ids;
            }

            var criteria = new EventQueryCriteria
            {
                Clauses = { clause },
                Level = EventLevels.ValueOf(SelectedLevel),
                StartTime = UseFrom ? FromDate : (DateTime?)null,
                EndTime = UseTo ? ToDate : (DateTime?)null,
                MaxEvents = MaxEvents,
                Keyword = (Keyword ?? "").Trim()
            };

            await RunAsync(token => _service.RunAsync(criteria, Progress(), token));
        }

        private async Task PresetSearchAsync(PresetDefinition preset)
        {
            if (preset == null) return;
            if (preset.RequiresElevation && !ConfirmElevation("Security")) return;

            // Reflect the preset's log in the filter panel, as clicking a preset did before.
            SelectedLogSource = preset.Clauses.FirstOrDefault()?.LogName ?? SelectedLogSource;
            EventIdText = "";

            var criteria = EventQueryCriteria.FromPreset(preset, MaxEvents, (Keyword ?? "").Trim());
            criteria.Level = EventLevels.ValueOf(SelectedLevel);
            criteria.StartTime = UseFrom ? FromDate : (DateTime?)null;
            criteria.EndTime = UseTo ? ToDate : (DateTime?)null;

            await RunAsync(token => _service.RunAsync(criteria, Progress(), token), preset.Label);
        }

        private async Task AppSearchAsync()
        {
            var app = (AppName ?? "").Trim();
            if (string.IsNullOrEmpty(app))
            {
                _ui.ShowError("Validation", "Please enter an application name.");
                return;
            }

            await RunAsync(token => _service.SearchApplicationAsync(
                app, MaxEvents, (Keyword ?? "").Trim(), Progress(), token));
        }

        private async Task SecuritySearchAsync()
        {
            var u = (SecUser ?? "").Trim();
            var h = (SecHost ?? "").Trim();
            var ip = (SecIp ?? "").Trim();

            if (u.Length == 0 && h.Length == 0 && ip.Length == 0)
            {
                _ui.ShowError("Validation", "Enter at least one of User, Host, or IP.");
                return;
            }

            if (!ConfirmElevation("Security")) return;

            await RunAsync(token => _service.SearchSecurityIdentityAsync(
                u, h, ip, MaxEvents, (Keyword ?? "").Trim(), Progress(), token));
        }

        private IProgress<int> Progress() =>
            new Progress<int>(n => StatusText = "Searching... " + n + " found");

        /// <summary>
        /// Shared search plumbing: cancellation, status, and mapping failures onto messages.
        /// </summary>
        private async Task RunAsync(Func<CancellationToken, Task<QueryResult>> run, string what = null)
        {
            if (IsSearching) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            IsSearching = true;
            StatusText = "Searching...";
            CountText = "";

            try
            {
                var result = await run(_cts.Token).ConfigureAwait(true);
                ShowResults(result, what);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            catch (LogAccessDeniedException)
            {
                StatusText = "Access denied";
                _ui.ShowError("Search Error",
                    "Access denied.\n\nThe Security log requires Administrator privileges.\n" +
                    "Run this tool as Administrator, or launch it from a Backstage session.");
            }
            catch (InvalidQueryException ex)
            {
                // A preset or a hand-edited presets.json is malformed. This is the one failure the
                // user can act on, so it names the log and the query rather than showing 0 rows.
                StatusText = "Invalid query";
                _ui.ShowError("Invalid Query",
                    "Windows rejected the query for log '" + ex.LogName + "'.\n\n" + ex.XPath +
                    "\n\nThis usually means a preset defines a log or provider that cannot be queried.");
            }
            catch (Exception ex)
            {
                StatusText = "Error";
                _ui.ShowError("Search Error", ex.Message);
            }
            finally
            {
                IsSearching = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void ShowResults(QueryResult result, string what)
        {
            Rows.Clear();
            foreach (var row in result.Rows) Rows.Add(row);

            LiveFilter = "";
            SelectedRow = null;
            RowsView.Refresh();

            if (result.WasCancelled)
            {
                StatusText = "Cancelled - showing " + result.Count + " partial result(s)";
            }
            else if (result.Count == 0)
            {
                StatusText = result.SkippedLogs.Count > 0
                    ? "0 records - log not present on this machine: " + string.Join(", ", result.SkippedLogs)
                    : "0 records found - try widening filters";
            }
            else
            {
                StatusText = what == null ? "Done" : "Done - " + what;
                if (result.SkippedLogs.Count > 0)
                    StatusText += " (skipped: " + string.Join(", ", result.SkippedLogs) + ")";
            }

            CountText = result.Count > 0 ? result.Count + " record(s)" : "";
            Raise(nameof(LiveCountText));
            RelayCommand.RaiseCanExecuteChanged();
        }

        private void Cancel()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            StatusText = "Cancelling...";
        }

        /// <summary>
        /// Warns before querying the Security log unelevated.
        /// </summary>
        /// <remarks>
        /// Skipped entirely when already elevated, which in Backstage means always - the process
        /// is SYSTEM, so the three prompts the PowerShell version showed would be pure noise on a
        /// desktop where they are also awkward to dismiss.
        /// </remarks>
        private bool ConfirmElevation(string logName)
        {
            if (!string.Equals(logName, "Security", StringComparison.OrdinalIgnoreCase)) return true;
            if (Host.IsElevated) return true;

            return _ui.Confirm("Elevation Required",
                "The Security log requires Administrator privileges.\n\n" +
                "Continue anyway (the query will likely fail)?");
        }

        internal static bool TryParseIds(string text, out List<int> ids)
        {
            ids = new List<int>();
            foreach (var part in (text ?? "").Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    return false;
                ids.Add(id);
            }
            return ids.Count > 0;
        }

        #endregion

        private void Clear()
        {
            SelectedLogSource = LogSources[0];
            SelectedLevel = Levels[0];
            EventIdText = "";
            Keyword = "";
            UseFrom = false;
            UseTo = false;
            MaxEvents = 1000;
            AppName = "";
            SecUser = "";
            SecHost = "";
            SecIp = "";
            LiveFilter = "";
            Rows.Clear();
            SelectedRow = null;
            StatusText = "Ready";
            CountText = "";
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Exports the rows currently visible in the grid, honouring the live filter.
        /// </summary>
        /// <remarks>
        /// On a Backstage desktop the shell save dialog is skipped entirely. Common file dialogs
        /// depend on the shell, which is not reliably available on an alternate desktop running as
        /// SYSTEM - the dialog can fail or hang rather than appear. Writing to a fixed, predictable
        /// path under the Windows temp directory is also the more useful behaviour there, since the
        /// technician retrieves the file over ScreenConnect's file transfer rather than by browsing.
        /// </remarks>
        private void Export()
        {
            var rows = RowsView.Cast<EventRow>().ToList();
            if (rows.Count == 0) return;

            var fileName = CsvExporter.DefaultFileName();

            try
            {
                string path = null;

                if (!Host.IsBackstageLikely)
                {
                    try
                    {
                        path = _ui.PromptSaveFile(fileName, "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*");
                        if (path == null) return;   // user cancelled
                    }
                    catch (Exception)
                    {
                        path = null;   // dialog unavailable; fall through to the fixed path
                    }
                }

                if (path == null)
                {
                    path = Path.Combine(Host.FallbackExportDirectory, fileName);
                    CsvExporter.Write(path, rows);
                    _ui.CopyToClipboard(path);
                    _ui.ShowInfo("Export Complete",
                        "Exported " + rows.Count + " record(s) to:\n\n" + path +
                        "\n\nThe path has been copied to the clipboard. Retrieve the file with " +
                        "ScreenConnect file transfer.");
                    return;
                }

                CsvExporter.Write(path, rows);
                _ui.ShowInfo("Export Complete", "Exported " + rows.Count + " record(s) to:\n\n" + path);
            }
            catch (Exception ex)
            {
                _ui.ShowError("Export Failed", ex.Message);
            }
        }

        private void EditPresets()
        {
            var editor = new PresetEditorViewModel(Store, _service, _ui);
            if (!_ui.ShowPresetEditor(editor)) return;

            Presets.Clear();
            foreach (var preset in Store.Presets) Presets.Add(preset);

            PresetWarning = null;
            StatusText = "Presets updated" +
                         (Store.SideCarPath != null ? " - saved to " + Store.SideCarPath : "");
        }
    }
}
