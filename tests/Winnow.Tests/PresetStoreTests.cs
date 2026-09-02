using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Winnow.Core.Presets;
using Winnow.Core.Query;
using Xunit;

namespace Winnow.Tests
{
    public class PresetStoreTests : IDisposable
    {
        private readonly string _dir;

        public PresetStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "elv-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string WriteSideCar(string json)
        {
            var path = Path.Combine(_dir, "presets.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void Defaults_LoadAllBuiltInPresets()
        {
            var store = PresetStore.LoadDefaults();

            Assert.Equal(36, store.Presets.Count);
            Assert.Null(store.LoadWarning);
        }

        [Fact]
        public void Defaults_HaveUniqueNonEmptyIds()
        {
            var ids = PresetStore.LoadDefaults().Presets.Select(p => p.Id).ToList();

            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void Defaults_EveryPresetHasAtLeastOneClauseWithALogName()
        {
            foreach (var preset in PresetStore.LoadDefaults().Presets)
            {
                Assert.NotEmpty(preset.Clauses);
                Assert.All(preset.Clauses, c => Assert.False(string.IsNullOrWhiteSpace(c.LogName)));
            }
        }

        [Fact]
        public void Defaults_PreserveTheKnownMultiLogPresets()
        {
            var store = PresetStore.LoadDefaults();

            // These two were the LogName2/Id2 special case in the PowerShell version.
            var multi = store.Presets.Where(p => p.Clauses.Count > 1).Select(p => p.Id).ToList();
            Assert.Equal(new[] { "resources.memory", "networking.dns-errors" }, multi);
        }

        [Fact]
        public void Defaults_PreserveProviderScopedPresets()
        {
            var store = PresetStore.LoadDefaults();
            var scoped = store.Presets
                .Where(p => p.Clauses.Any(c => c.ProviderNames.Any()))
                .Select(p => p.Id).ToList();

            // Losing any of these would silently widen the preset to every source sharing the ID.
            Assert.Equal(new[]
            {
                "resources.disk-errors",
                "hardware.whea",
                "hardware.unexpected-shutdown",
                "hardware.bsod-bugcheck"
            }, scoped);
        }

        [Fact]
        public void Defaults_PreserveMessageFilteredPresets()
        {
            var filtered = PresetStore.LoadDefaults().Presets
                .Where(p => !string.IsNullOrEmpty(p.MessageFilter))
                .ToDictionary(p => p.Id, p => p.MessageFilter);

            Assert.Equal("driver", filtered["system.driver-installs"]);
            Assert.Equal("Spooler", filtered["printing.spooler-events"]);
            Assert.Equal(2, filtered.Count);
        }

        [Fact]
        public void SecurityPresets_AreFlaggedAsNeedingElevation()
        {
            var store = PresetStore.LoadDefaults();
            var elevated = store.Presets.Where(p => p.RequiresElevation).ToList();

            Assert.Equal(11, elevated.Count);
            Assert.Contains(elevated, p => p.Id == "account.audit-log-cleared");
        }

        [Fact]
        public void NoSideCar_YieldsDefaultsOnly()
        {
            var store = PresetStore.Load(null);
            Assert.Equal(36, store.Presets.Count);
            Assert.Null(store.LoadWarning);
        }

        [Fact]
        public void MissingSideCarFile_IsNotAnError()
        {
            var store = PresetStore.Load(Path.Combine(_dir, "does-not-exist.json"));

            Assert.Equal(36, store.Presets.Count);
            Assert.Null(store.LoadWarning);
        }

        [Fact]
        public void SideCar_OverridesBuiltInById()
        {
            var path = WriteSideCar(@"{ ""presets"": [
                { ""id"": ""system.service-changes"", ""group"": ""System Changes"", ""label"": ""Services"",
                  ""clauses"": [ { ""logName"": ""System"", ""eventIds"": [7045] } ] } ] }");

            var store = PresetStore.Load(path);
            var preset = store.Presets.Single(p => p.Id == "system.service-changes");

            Assert.Equal("Services", preset.Label);
            Assert.Equal(new[] { 7045 }, preset.Clauses.Single().EventIds);
            Assert.Equal(36, store.Presets.Count);          // replaced, not added
            Assert.True(store.IsOverridden(preset));
        }

        [Fact]
        public void SideCar_AddsUnknownId()
        {
            var path = WriteSideCar(@"{ ""presets"": [
                { ""id"": ""custom.my-app"", ""group"": ""Custom"", ""label"": ""My App"",
                  ""clauses"": [ { ""logName"": ""Application"", ""eventIds"": [42] } ] } ] }");

            var store = PresetStore.Load(path);

            Assert.Equal(37, store.Presets.Count);
            var added = store.Presets.Single(p => p.Id == "custom.my-app");
            Assert.False(store.IsBuiltIn(added));
        }

        [Fact]
        public void SideCar_DisablesBuiltIn()
        {
            var path = WriteSideCar(@"{ ""presets"": [
                { ""id"": ""printing.print-jobs"", ""disabled"": true } ] }");

            var store = PresetStore.Load(path);

            Assert.Equal(35, store.Presets.Count);
            Assert.DoesNotContain(store.Presets, p => p.Id == "printing.print-jobs");
            // Still present in the full list, so the editor can offer to turn it back on.
            Assert.Contains(store.AllPresets, p => p.Id == "printing.print-jobs");
        }

        [Fact]
        public void DisableEntry_DoesNotHaveToRestateTheWholePreset()
        {
            var path = WriteSideCar(@"{ ""presets"": [
                { ""id"": ""printing.print-jobs"", ""disabled"": true } ] }");

            var preset = PresetStore.Load(path).AllPresets.Single(p => p.Id == "printing.print-jobs");

            // The clauses and label survive, so re-enabling it restores a working preset.
            Assert.Equal("Print Jobs", preset.Label);
            Assert.NotEmpty(preset.Clauses);
            Assert.Equal(307, preset.Clauses.Single().EventIds.Single());
        }

        [Fact]
        public void MalformedSideCar_FallsBackToDefaultsWithAWarning()
        {
            var path = WriteSideCar("{ this is not json ");

            var store = PresetStore.Load(path);

            Assert.Equal(36, store.Presets.Count);
            Assert.NotNull(store.LoadWarning);
            Assert.Contains("presets.json", store.LoadWarning);
        }

        [Fact]
        public void SideCarWithoutPresetsArray_FallsBackWithAWarning()
        {
            var path = WriteSideCar(@"{ ""somethingElse"": 1 }");

            var store = PresetStore.Load(path);

            Assert.Equal(36, store.Presets.Count);
            Assert.NotNull(store.LoadWarning);
        }

        [Fact]
        public void SideCarEntryWithoutId_IsIgnored()
        {
            var path = WriteSideCar(@"{ ""presets"": [
                { ""label"": ""No Id"", ""clauses"": [ { ""logName"": ""System"" } ] } ] }");

            var store = PresetStore.Load(path);

            Assert.Equal(36, store.Presets.Count);
        }

        [Fact]
        public void Save_WritesOnlyTheDelta()
        {
            var store = PresetStore.LoadDefaults();
            var presets = store.AllPresets.Select(p => p.Clone()).ToList();
            presets.Single(p => p.Id == "system.service-changes").Label = "Renamed";
            presets.Add(new PresetDefinition
            {
                Id = "custom.new",
                Group = "Custom",
                Label = "New",
                Clauses = { new QueryClause { LogName = "Application", EventIds = { 1 } } }
            });
            store.ReplaceAll(presets);

            var path = Path.Combine(_dir, "out.json");
            store.Save(path);

            var written = PresetStore.Deserialize(File.ReadAllText(path));

            // One changed built-in plus one custom preset - not all 37.
            Assert.Equal(2, written.Presets.Count);
            Assert.Contains(written.Presets, p => p.Id == "system.service-changes" && p.Label == "Renamed");
            Assert.Contains(written.Presets, p => p.Id == "custom.new");
        }

        [Fact]
        public void Save_WithNoChanges_WritesAnEmptyDelta()
        {
            var store = PresetStore.LoadDefaults();
            var path = Path.Combine(_dir, "empty.json");

            store.Save(path);

            Assert.Empty(PresetStore.Deserialize(File.ReadAllText(path)).Presets);
        }

        [Fact]
        public void Save_PersistsARemovedBuiltInAsADisable()
        {
            var store = PresetStore.LoadDefaults();
            var presets = store.AllPresets.Select(p => p.Clone())
                .Where(p => p.Id != "ad.dns-server").ToList();
            store.ReplaceAll(presets);

            var path = Path.Combine(_dir, "removed.json");
            store.Save(path);

            var tombstone = PresetStore.Deserialize(File.ReadAllText(path))
                .Presets.Single(p => p.Id == "ad.dns-server");
            Assert.True(tombstone.Disabled);

            // And it stays gone after a reload, rather than silently reappearing.
            Assert.DoesNotContain(PresetStore.Load(path).Presets, p => p.Id == "ad.dns-server");
        }

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            var store = PresetStore.LoadDefaults();
            var presets = store.AllPresets.Select(p => p.Clone()).ToList();
            presets.Single(p => p.Id == "hardware.whea").Clauses.Single().EventIds.Add(2);
            store.ReplaceAll(presets);

            var path = Path.Combine(_dir, "roundtrip.json");
            store.Save(path);

            var reloaded = PresetStore.Load(path).Presets.Single(p => p.Id == "hardware.whea");
            Assert.Equal(new[] { 1, 2 }, reloaded.Clauses.Single().EventIds);
            Assert.Equal(new[] { "Microsoft-Windows-WHEA-Logger", "Microsoft-Windows-Kernel-WHEA" },
                         reloaded.Clauses.Single().ProviderNames);
        }

        [Fact]
        public void EveryDefaultPreset_ProducesANonEmptyXPath()
        {
            foreach (var preset in PresetStore.LoadDefaults().Presets)
            {
                foreach (var clause in preset.Clauses)
                {
                    var xpath = EventXPath.Build(clause);
                    Assert.False(string.IsNullOrWhiteSpace(xpath));
                }
            }
        }
    }
}
