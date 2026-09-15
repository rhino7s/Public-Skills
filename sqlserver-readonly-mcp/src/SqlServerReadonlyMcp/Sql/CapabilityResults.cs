using System.Text.Json.Serialization;

namespace SqlServerReadonlyMcp.Sql;

public sealed record CapabilityItem(int Id, string? Iname, string Summary,
    [property: JsonPropertyName("has_desp")] bool HasDescription);
public sealed record CapabilityPage(IReadOnlyList<CapabilityItem> Items,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("next_offset")] long? NextOffset);
public sealed record CapabilityDetailsResult(int Id, string? Iname, string Summary,
    [property: JsonPropertyName("has_desp")] bool HasDescription, string Desp);
