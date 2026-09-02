using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace EventLogViewer.Core.Presets
{
    /// <summary>
    /// Owns the effective preset list: built-in defaults embedded in the assembly, with an
    /// optional side-car presets.json merged over the top.
    /// </summary>
    /// <remarks>
    /// Loading follows the same philosophy as the update check - a broken or unreadable side-car
    /// must degrade to "built-in presets, plus a visible warning" and never take the app down.
    /// A tech with a typo in presets.json still needs a working event log viewer.
    /// </remarks>
    public sealed class PresetStore
    {
        internal const string EmbeddedResourceName = "EventLogViewer.Core.Presets.DefaultPresets.json";
        public const string SideCarFileName = "presets.json";

        private readonly List<PresetDefinition> _builtIn;
        private List<PresetDefinition> _effective;

        private PresetStore(List<PresetDefinition> builtIn)
        {
            _builtIn = builtIn;
            _effective = builtIn.Select(p => p.Clone()).ToList();
        }

        /// <summary>Every preset the UI should show, in declaration order, with disabled ones removed.</summary>
        public IReadOnlyList<PresetDefinition> Presets =>
            _effective.Where(p => !p.Disabled).ToList();

        /// <summary>All presets including disabled ones - what the preset editor binds to.</summary>
        public IReadOnlyList<PresetDefinition> AllPresets => _effective;

        /// <summary>Unmodified built-in set, so the editor can offer "reset to default".</summary>
        public IReadOnlyList<PresetDefinition> BuiltIn => _builtIn;

        /// <summary>Path the side-car was loaded from, and where Save() writes. Null if none.</summary>
        public string SideCarPath { get; private set; }

        /// <summary>Non-null when the side-car existed but could not be used. Surface this in the
        /// UI - a silently ignored preset file is worse than a broken one.</summary>
        public string LoadWarning { get; private set; }

        public bool IsBuiltIn(PresetDefinition preset) =>
            preset != null && _builtIn.Any(b => IdEquals(b.Id, preset.Id));

        /// <summary>True when a built-in has been changed or hidden by the side-car.</summary>
        public bool IsOverridden(PresetDefinition preset)
        {
            if (preset == null) return false;
            var b = _builtIn.FirstOrDefault(x => IdEquals(x.Id, preset.Id));
            return b != null && !AreEquivalent(b, preset);
        }

        public static PresetStore LoadDefaults() => new PresetStore(ReadEmbeddedDefaults());

        /// <summary>
        /// Loads built-ins and merges the side-car at <paramref name="sideCarPath"/> if it exists.
        /// A null/blank path, or a missing file, is not an error - it just means "defaults only".
        /// </summary>
        public static PresetStore Load(string sideCarPath)
        {
            var store = new PresetStore(ReadEmbeddedDefaults());

            if (string.IsNullOrWhiteSpace(sideCarPath))
                return store;

            store.SideCarPath = sideCarPath;

            if (!File.Exists(sideCarPath))
                return store;   // a path that doesn't exist yet is where Save() will create one

            try
            {
                var text = File.ReadAllText(sideCarPath);

                // Checked against the raw JSON rather than the deserialized object: PresetDocument
                // initialises Presets to an empty list, so a file missing the key entirely and a
                // file with "presets": [] are indistinguishable after deserialization. The first
                // is almost always a typo worth warning about; the second is a legitimate
                // "no overrides" file.
                if (!HasPresetsArray(text))
                {
                    store.LoadWarning = "Preset file '" + Path.GetFileName(sideCarPath) +
                                        "' has no presets array; using built-in presets.";
                    return store;
                }

                var doc = Deserialize(text);
                if (doc == null || doc.Presets == null)
                {
                    store.LoadWarning = "Preset file '" + Path.GetFileName(sideCarPath) +
                                        "' could not be read; using built-in presets.";
                    return store;
                }
                store.Merge(doc.Presets);
            }
            catch (Exception ex)
            {
                store.LoadWarning = "Could not read '" + Path.GetFileName(sideCarPath) +
                                    "' (" + ex.Message + "); using built-in presets.";
            }

            return store;
        }

        /// <summary>
        /// Merge by id: a matching id replaces the built-in, an unknown id is appended, and
        /// disabled:true hides a built-in without removing it.
        /// </summary>
        private void Merge(IEnumerable<PresetDefinition> overrides)
        {
            foreach (var ov in overrides)
            {
                if (ov == null || string.IsNullOrWhiteSpace(ov.Id)) continue;

                var index = _effective.FindIndex(p => IdEquals(p.Id, ov.Id));
                if (index >= 0)
                {
                    // A side-car entry that only turns a built-in off shouldn't have to restate the
                    // whole preset, so an entry with no clauses inherits the built-in's definition.
                    var merged = ov.Clone();
                    if (merged.Clauses == null || merged.Clauses.Count == 0)
                    {
                        var existing = _effective[index];
                        merged.Clauses = existing.Clauses.Select(c => c.Clone()).ToList();
                        if (string.IsNullOrEmpty(merged.Group)) merged.Group = existing.Group;
                        if (string.IsNullOrEmpty(merged.Label)) merged.Label = existing.Label;
                        if (string.IsNullOrEmpty(merged.Description)) merged.Description = existing.Description;
                        if (string.IsNullOrEmpty(merged.MessageFilter)) merged.MessageFilter = existing.MessageFilter;
                    }
                    _effective[index] = merged;
                }
                else
                {
                    _effective.Add(ov.Clone());
                }
            }
        }

        /// <summary>Replaces the in-memory set (used by the preset editor before saving).</summary>
        public void ReplaceAll(IEnumerable<PresetDefinition> presets)
        {
            _effective = presets == null
                ? new List<PresetDefinition>()
                : presets.Select(p => p.Clone()).ToList();
        }

        /// <summary>
        /// Computes the side-car delta: presets that differ from their built-in, plus custom
        /// presets, plus disables. Writing only the delta keeps the file small and reviewable, and
        /// means a later fix to a built-in still reaches users who never touched that preset.
        /// </summary>
        public PresetDocument BuildDelta()
        {
            var delta = new PresetDocument
            {
                Comment = "Overrides for EventLogViewer built-in presets. A matching id replaces a " +
                          "built-in, a new id adds a preset, and disabled:true hides a built-in."
            };

            foreach (var p in _effective)
            {
                var builtIn = _builtIn.FirstOrDefault(b => IdEquals(b.Id, p.Id));
                if (builtIn == null || !AreEquivalent(builtIn, p))
                    delta.Presets.Add(p.Clone());
            }

            // A built-in removed outright in the editor is persisted as a disable, otherwise it
            // would silently reappear on the next launch.
            foreach (var b in _builtIn)
            {
                if (_effective.Any(p => IdEquals(p.Id, b.Id))) continue;
                var tombstone = b.Clone();
                tombstone.Disabled = true;
                tombstone.Clauses = new List<QueryClause>();
                delta.Presets.Add(tombstone);
            }

            return delta;
        }

        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A preset file path is required.", "path");

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, Serialize(BuildDelta()));
            SideCarPath = path;
        }

        public static string Serialize(PresetDocument doc) =>
            JsonConvert.SerializeObject(doc, Formatting.Indented, SerializerSettings());

        public static PresetDocument Deserialize(string json) =>
            JsonConvert.DeserializeObject<PresetDocument>(json, SerializerSettings());

        private static JsonSerializerSettings SerializerSettings() => new JsonSerializerSettings
        {
            // presets.json is meant to be hand-edited, so tolerate stray nulls and unknown keys.
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        private static List<PresetDefinition> ReadEmbeddedDefaults()
        {
            var asm = typeof(PresetStore).Assembly;
            using (var stream = asm.GetManifestResourceStream(EmbeddedResourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "Embedded resource '" + EmbeddedResourceName + "' is missing. Available: " +
                        string.Join(", ", asm.GetManifestResourceNames()));
                }
                using (var reader = new StreamReader(stream))
                {
                    var doc = Deserialize(reader.ReadToEnd());
                    return doc == null || doc.Presets == null
                        ? new List<PresetDefinition>()
                        : doc.Presets;
                }
            }
        }

        private static bool HasPresetsArray(string json)
        {
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
            var token = parsed["presets"];
            return token != null && token.Type == Newtonsoft.Json.Linq.JTokenType.Array;
        }

        private static bool IdEquals(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Value comparison, used to decide whether a preset belongs in the delta.</summary>
        internal static bool AreEquivalent(PresetDefinition a, PresetDefinition b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (!IdEquals(a.Id, b.Id)) return false;
            if (a.Group != b.Group || a.Label != b.Label || a.Description != b.Description) return false;
            if (a.Disabled != b.Disabled) return false;
            if ((a.MessageFilter ?? "") != (b.MessageFilter ?? "")) return false;

            var ac = a.Clauses ?? new List<QueryClause>();
            var bc = b.Clauses ?? new List<QueryClause>();
            if (ac.Count != bc.Count) return false;

            for (int i = 0; i < ac.Count; i++)
            {
                if (ac[i].LogName != bc[i].LogName) return false;
                if (!(ac[i].EventIds ?? new List<int>()).SequenceEqual(bc[i].EventIds ?? new List<int>())) return false;
                if (!(ac[i].ProviderNames ?? new List<string>()).SequenceEqual(bc[i].ProviderNames ?? new List<string>())) return false;
            }
            return true;
        }

        /// <summary>
        /// Side-car resolution: an explicit --presets path wins, otherwise presets.json beside the
        /// executable. Returns null when neither applies.
        /// </summary>
        public static string ResolveSideCarPath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return Path.GetFullPath(explicitPath);

            try
            {
                var entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var exeDir = Path.GetDirectoryName(entry.Location);
                if (string.IsNullOrEmpty(exeDir)) return null;
                var candidate = Path.Combine(exeDir, SideCarFileName);
                return File.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;   // running from somewhere with no resolvable location
            }
        }
    }
}
