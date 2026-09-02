using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Winnow.Core.Presets
{
    /// <summary>
    /// One quick-filter preset. A preset is a list of <see cref="QueryClause"/>s whose results are
    /// merged and sorted by time, optionally narrowed by a message substring.
    /// </summary>
    /// <remarks>
    /// The clause list replaces the original script's LogName/Id plus optional LogName2/Id2 pair,
    /// which could only ever express exactly two logs. Presets that span more than one log
    /// (Resource/Memory, DNS Errors) are ordinary two-clause presets here, and a third log costs
    /// nothing to add.
    /// </remarks>
    public sealed class PresetDefinition
    {
        /// <summary>Stable identity, e.g. "system.software-installs". Side-car files match on this,
        /// so it must never change once released - the label is free to change, this is not.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("group")]
        public string Group { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary>Set by a side-car to hide a built-in preset without deleting it.</summary>
        [JsonProperty("disabled")]
        public bool Disabled { get; set; }

        [JsonProperty("clauses")]
        public List<QueryClause> Clauses { get; set; } = new List<QueryClause>();

        /// <summary>
        /// Case-insensitive substring applied to the rendered message after the query returns.
        /// Needed where an Event ID is shared across unrelated sources with no distinguishing
        /// provider - only the message text says which one it is (e.g. Service Control Manager's
        /// 7031/7034, which every service on the box emits).
        /// </summary>
        [JsonProperty("messageFilter")]
        public string MessageFilter { get; set; }

        /// <summary>True when this preset needs the Security log, i.e. needs elevation.</summary>
        [JsonIgnore]
        public bool RequiresElevation =>
            Clauses != null &&
            Clauses.Any(c => string.Equals(c.LogName, "Security", System.StringComparison.OrdinalIgnoreCase));

        public PresetDefinition Clone() => new PresetDefinition
        {
            Id = Id,
            Group = Group,
            Label = Label,
            Description = Description,
            Disabled = Disabled,
            MessageFilter = MessageFilter,
            Clauses = Clauses?.Select(c => c.Clone()).ToList() ?? new List<QueryClause>()
        };

        public override string ToString() => $"{Group}/{Label} ({Id})";
    }

    /// <summary>One (log, event ids, providers) tuple within a preset.</summary>
    public sealed class QueryClause
    {
        [JsonProperty("logName")]
        public string LogName { get; set; }

        /// <summary>Empty or null means "every event in this log", which is how the
        /// Domain-Controller-only presets (Directory Service, DFS Replication, DNS Server) work -
        /// their event IDs vary too much to enumerate reliably.</summary>
        [JsonProperty("eventIds")]
        public List<int> EventIds { get; set; } = new List<int>();

        /// <summary>Scopes the clause to specific providers. Essential where a low-numbered ID is
        /// reused across dozens of unrelated sources in the same log.</summary>
        [JsonProperty("providerNames")]
        public List<string> ProviderNames { get; set; } = new List<string>();

        public QueryClause Clone() => new QueryClause
        {
            LogName = LogName,
            EventIds = EventIds != null ? new List<int>(EventIds) : new List<int>(),
            ProviderNames = ProviderNames != null ? new List<string>(ProviderNames) : new List<string>()
        };

        public override string ToString() => LogName;
    }
}
