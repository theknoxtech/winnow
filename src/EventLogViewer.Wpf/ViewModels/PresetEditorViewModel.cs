using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using EventLogViewer.Core.Presets;
using EventLogViewer.Core.Query;

namespace EventLogViewer.Wpf.ViewModels
{
    /// <summary>One clause, with the id and provider lists surfaced as editable text.</summary>
    public sealed class ClauseViewModel : ViewModelBase
    {
        public ClauseViewModel() { }

        public ClauseViewModel(QueryClause clause)
        {
            _logName = clause.LogName ?? "";
            _eventIdsText = string.Join(", ", (clause.EventIds ?? new List<int>())
                .Select(i => i.ToString(CultureInfo.InvariantCulture)));
            _providersText = string.Join(", ", clause.ProviderNames ?? new List<string>());
        }

        private string _logName = "";
        public string LogName { get => _logName; set => Set(ref _logName, value); }

        private string _eventIdsText = "";
        /// <summary>Comma-separated. Blank means every event in the log.</summary>
        public string EventIdsText { get => _eventIdsText; set => Set(ref _eventIdsText, value); }

        private string _providersText = "";
        public string ProvidersText { get => _providersText; set => Set(ref _providersText, value); }

        public bool TryBuild(out QueryClause clause, out string error)
        {
            clause = null;
            error = null;

            if (string.IsNullOrWhiteSpace(LogName))
            {
                error = "Every clause needs a log name.";
                return false;
            }

            var ids = new List<int>();
            foreach (var part in (EventIdsText ?? "").Split(','))
            {
                var t = part.Trim();
                if (t.Length == 0) continue;
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    error = "'" + t + "' is not a valid Event ID.";
                    return false;
                }
                ids.Add(id);
            }

            var providers = (ProvidersText ?? "")
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            clause = new QueryClause
            {
                LogName = LogName.Trim(),
                EventIds = ids,
                ProviderNames = providers
            };
            return true;
        }
    }

    /// <summary>A preset in the editor, with its origin surfaced for the list.</summary>
    public sealed class PresetItemViewModel : ViewModelBase
    {
        public PresetItemViewModel(PresetDefinition preset, PresetStore store)
        {
            Id = preset.Id;
            _group = preset.Group ?? "";
            _label = preset.Label ?? "";
            _description = preset.Description ?? "";
            _messageFilter = preset.MessageFilter ?? "";
            _isEnabled = !preset.Disabled;

            Clauses = new ObservableCollection<ClauseViewModel>(
                (preset.Clauses ?? new List<QueryClause>()).Select(c => new ClauseViewModel(c)));

            IsBuiltIn = store.IsBuiltIn(preset);
        }

        public PresetItemViewModel(string id)
        {
            Id = id;
            Clauses = new ObservableCollection<ClauseViewModel> { new ClauseViewModel() };
            _isEnabled = true;
            IsBuiltIn = false;
        }

        public string Id { get; }
        public bool IsBuiltIn { get; }

        private string _group = "";
        public string Group { get => _group; set { if (Set(ref _group, value)) Raise(nameof(Origin)); } }

        private string _label = "";
        public string Label { get => _label; set { if (Set(ref _label, value)) Raise(nameof(DisplayName)); } }

        private string _description = "";
        public string Description { get => _description; set => Set(ref _description, value); }

        private string _messageFilter = "";
        public string MessageFilter { get => _messageFilter; set => Set(ref _messageFilter, value); }

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (Set(ref _isEnabled, value)) Raise(nameof(DisplayName)); }
        }

        public ObservableCollection<ClauseViewModel> Clauses { get; }

        public string DisplayName => (_isEnabled ? "" : "(off) ") + _label;

        public string Origin => IsBuiltIn ? "built-in" : "custom";

        public bool TryBuild(out PresetDefinition preset, out string error)
        {
            preset = null;
            error = null;

            if (string.IsNullOrWhiteSpace(Label))
            {
                error = "A preset needs a label.";
                return false;
            }
            if (Clauses.Count == 0)
            {
                error = "A preset needs at least one clause.";
                return false;
            }

            var clauses = new List<QueryClause>();
            foreach (var c in Clauses)
            {
                if (!c.TryBuild(out var clause, out error)) return false;
                clauses.Add(clause);
            }

            preset = new PresetDefinition
            {
                Id = Id,
                Group = string.IsNullOrWhiteSpace(Group) ? "Custom" : Group.Trim(),
                Label = Label.Trim(),
                Description = (Description ?? "").Trim(),
                MessageFilter = string.IsNullOrWhiteSpace(MessageFilter) ? null : MessageFilter.Trim(),
                Disabled = !IsEnabled,
                Clauses = clauses
            };
            return true;
        }
    }

    public sealed class PresetEditorViewModel : ViewModelBase
    {
        private readonly PresetStore _store;
        private readonly EventLogService _service;
        private readonly IUserInteraction _ui;

        public PresetEditorViewModel(PresetStore store, EventLogService service, IUserInteraction ui)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));

            Items = new ObservableCollection<PresetItemViewModel>(
                store.AllPresets.Select(p => new PresetItemViewModel(p, store)));
            Selected = Items.FirstOrDefault();

            AddCommand = new RelayCommand(Add);
            CloneCommand = new RelayCommand(Clone, () => Selected != null);
            DeleteCommand = new RelayCommand(Delete, () => Selected != null);
            ResetCommand = new RelayCommand(ResetToBuiltIn, () => Selected?.IsBuiltIn == true);
            AddClauseCommand = new RelayCommand(AddClause, () => Selected != null);
            RemoveClauseCommand = new RelayCommand(RemoveClause, () => Selected?.Clauses.Count > 1);
            TestCommand = new RelayCommand(Test, () => Selected != null);
            ImportCommand = new RelayCommand(Import);
            ExportCommand = new RelayCommand(ExportFile, () => Items.Count > 0);
        }

        public ObservableCollection<PresetItemViewModel> Items { get; }

        private PresetItemViewModel _selected;
        public PresetItemViewModel Selected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value)) RelayCommand.RaiseCanExecuteChanged();
            }
        }

        private string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }

        public RelayCommand AddCommand { get; }
        public RelayCommand CloneCommand { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand ResetCommand { get; }
        public RelayCommand AddClauseCommand { get; }
        public RelayCommand RemoveClauseCommand { get; }
        public RelayCommand TestCommand { get; }
        public RelayCommand ImportCommand { get; }
        public RelayCommand ExportCommand { get; }

        private void Add()
        {
            var item = new PresetItemViewModel(NewId("custom.preset"))
            {
                Group = "Custom",
                Label = "New Preset"
            };
            item.Clauses[0].LogName = "Application";
            Items.Add(item);
            Selected = item;
            Status = "Added a new preset.";
        }

        private void Clone()
        {
            if (Selected == null) return;

            var copy = new PresetItemViewModel(NewId(Selected.Id))
            {
                Group = Selected.Group,
                Label = Selected.Label + " (copy)",
                Description = Selected.Description,
                MessageFilter = Selected.MessageFilter
            };
            copy.Clauses.Clear();
            foreach (var c in Selected.Clauses)
                copy.Clauses.Add(new ClauseViewModel
                {
                    LogName = c.LogName,
                    EventIdsText = c.EventIdsText,
                    ProvidersText = c.ProvidersText
                });

            Items.Add(copy);
            Selected = copy;
            Status = "Cloned to a new custom preset.";
        }

        /// <summary>
        /// Deleting a built-in turns it off instead of removing it, so it can be brought back and
        /// so a future change to that built-in is not permanently discarded.
        /// </summary>
        private void Delete()
        {
            if (Selected == null) return;

            if (Selected.IsBuiltIn)
            {
                Selected.IsEnabled = false;
                Status = "Built-in preset turned off. Re-enable it with the Enabled box.";
                return;
            }

            if (!_ui.Confirm("Delete preset", "Delete '" + Selected.Label + "'?")) return;

            var index = Items.IndexOf(Selected);
            Items.Remove(Selected);
            Selected = Items.ElementAtOrDefault(Math.Min(index, Items.Count - 1));
            Status = "Preset deleted.";
        }

        private void ResetToBuiltIn()
        {
            if (Selected?.IsBuiltIn != true) return;

            var original = _store.BuiltIn.FirstOrDefault(b =>
                string.Equals(b.Id, Selected.Id, StringComparison.OrdinalIgnoreCase));
            if (original == null) return;

            var index = Items.IndexOf(Selected);
            var restored = new PresetItemViewModel(original, _store);
            Items[index] = restored;
            Selected = restored;
            Status = "Reset to the built-in definition.";
        }

        private void AddClause()
        {
            Selected?.Clauses.Add(new ClauseViewModel { LogName = "Application" });
            RelayCommand.RaiseCanExecuteChanged();
        }

        private void RemoveClause()
        {
            if (Selected == null || Selected.Clauses.Count <= 1) return;
            Selected.Clauses.RemoveAt(Selected.Clauses.Count - 1);
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Runs the preset as currently edited and reports the hit count - by far the quickest way
        /// to notice a wrong event ID or a log name that does not exist on this machine.
        /// </summary>
        private void Test()
        {
            if (Selected == null) return;

            if (!Selected.TryBuild(out var preset, out var error))
            {
                Status = error;
                return;
            }

            try
            {
                Status = "Testing...";
                var criteria = EventQueryCriteria.FromPreset(preset, 200, null);
                var result = _service.Run(criteria, null, CancellationToken.None);

                Status = result.SkippedLogs.Count > 0
                    ? result.Count + " match(es); log not on this machine: " +
                      string.Join(", ", result.SkippedLogs)
                    : result.Count + " match(es) in the most recent 200 events per log.";
            }
            catch (LogAccessDeniedException)
            {
                Status = "Access denied - this preset reads the Security log and needs elevation.";
            }
            catch (InvalidQueryException ex)
            {
                Status = "Windows rejected the query: " + ex.XPath;
            }
            catch (Exception ex)
            {
                Status = "Test failed: " + ex.Message;
            }
        }

        private void Import()
        {
            try
            {
                var path = _ui.PromptOpenFile("Preset files (*.json)|*.json|All Files (*.*)|*.*");
                if (path == null || !File.Exists(path))
                {
                    Status = "Import cancelled or file not found.";
                    return;
                }

                var doc = PresetStore.Deserialize(File.ReadAllText(path));
                if (doc?.Presets == null || doc.Presets.Count == 0)
                {
                    Status = "That file contains no presets.";
                    return;
                }

                foreach (var incoming in doc.Presets)
                {
                    if (string.IsNullOrWhiteSpace(incoming.Id)) continue;

                    var existing = Items.FirstOrDefault(i =>
                        string.Equals(i.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
                    var replacement = new PresetItemViewModel(incoming, _store);

                    if (existing != null) Items[Items.IndexOf(existing)] = replacement;
                    else Items.Add(replacement);
                }

                Selected = Items.FirstOrDefault();
                Status = "Imported " + doc.Presets.Count + " preset(s) from " + Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                Status = "Import failed: " + ex.Message;
            }
        }

        private void ExportFile()
        {
            if (!TryApply(out var error))
            {
                Status = error;
                return;
            }

            try
            {
                var path = _ui.PromptSaveFile("presets.json", "Preset files (*.json)|*.json|All Files (*.*)|*.*");
                if (path == null) return;

                _store.Save(path);
                Status = "Exported to " + path;
            }
            catch (Exception ex)
            {
                Status = "Export failed: " + ex.Message;
            }
        }

        /// <summary>Validates every preset and pushes them into the store, without saving to disk.</summary>
        public bool TryApply(out string error)
        {
            error = null;
            var built = new List<PresetDefinition>();

            foreach (var item in Items)
            {
                if (!item.TryBuild(out var preset, out var itemError))
                {
                    error = "'" + (item.Label ?? item.Id) + "': " + itemError;
                    return false;
                }
                built.Add(preset);
            }

            var duplicate = built.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                                 .FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                error = "Two presets share the id '" + duplicate.Key + "'.";
                return false;
            }

            _store.ReplaceAll(built);
            return true;
        }

        /// <summary>
        /// Applies and writes the side-car. Returns false if the user needs to pick a location or
        /// something went wrong.
        /// </summary>
        public bool Save()
        {
            if (!TryApply(out var error))
            {
                Status = error;
                _ui.ShowError("Cannot save presets", error);
                return false;
            }

            var path = _store.SideCarPath ?? DefaultSideCarPath();

            try
            {
                _store.Save(path);
                return true;
            }
            catch (Exception ex)
            {
                // The exe is often run from a temp copy or a read-only share, so a failure here is
                // expected rather than exceptional - offer somewhere else to put it.
                _ui.ShowError("Cannot write preset file",
                    "Could not write to:\n" + path + "\n\n" + ex.Message +
                    "\n\nChoose another location.");

                try
                {
                    var chosen = _ui.PromptSaveFile("presets.json",
                        "Preset files (*.json)|*.json|All Files (*.*)|*.*");
                    if (chosen == null) return false;

                    _store.Save(chosen);
                    return true;
                }
                catch (Exception inner)
                {
                    _ui.ShowError("Cannot write preset file", inner.Message);
                    return false;
                }
            }
        }

        private static string DefaultSideCarPath()
        {
            var exeDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetEntryAssembly()?.Location ?? ".");
            return Path.Combine(exeDir ?? ".", PresetStore.SideCarFileName);
        }

        /// <summary>Derives a unique, readable id so hand-editing the file later stays pleasant.</summary>
        private string NewId(string basis)
        {
            var root = Regex.Replace((basis ?? "custom.preset").ToLowerInvariant(), "[^a-z0-9.-]+", "-")
                            .Trim('-');
            if (root.Length == 0) root = "custom.preset";

            var candidate = root;
            var n = 2;
            while (Items.Any(i => string.Equals(i.Id, candidate, StringComparison.OrdinalIgnoreCase)))
                candidate = root + "-" + n++;

            return candidate;
        }
    }
}
