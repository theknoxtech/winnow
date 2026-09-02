using System.Collections.Generic;
using Newtonsoft.Json;

namespace EventLogViewer.Core.Presets
{
    /// <summary>Wire format of DefaultPresets.json and of a side-car presets.json.</summary>
    public sealed class PresetDocument
    {
        /// <summary>Free-text note; present so hand-edited files can carry an explanation.
        /// Newtonsoft would ignore an unmapped "$comment" anyway, but naming it keeps a
        /// round-trip through Save() from silently dropping it.</summary>
        [JsonProperty("$comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        [JsonProperty("presets")]
        public List<PresetDefinition> Presets { get; set; } = new List<PresetDefinition>();
    }
}
