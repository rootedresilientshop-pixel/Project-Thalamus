using System.Text.Json.Serialization;

namespace Thalamus.Core;

/// <summary>
/// Describes a node type. Serializable to/from JSON so schemas can be
/// persisted, diffed, or transmitted across process boundaries if needed.
/// </summary>
public sealed record NodeSchema
{
    /// <summary>Unique name within the provider, e.g. "Xorshift32".</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Optional human-readable description shown in the UI tooltip.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Category label used for grouping in the palette, e.g. "Math".</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = "General";

    /// <summary>Ordered list of input port declarations.</summary>
    [JsonPropertyName("inputs")]
    public IReadOnlyList<PortSchema> Inputs { get; init; } = [];

    /// <summary>Ordered list of output port declarations.</summary>
    [JsonPropertyName("outputs")]
    public IReadOnlyList<PortSchema> Outputs { get; init; } = [];

    /// <summary>Provider name (e.g. "BridgeMod", "Kanon"). Set by MainViewModel when building the palette. Not serialized to file.</summary>
    [JsonIgnore]
    public string? ProviderName { get; init; }

    /// <summary>Icon name for visual identity (e.g. "bolt", "shield", "calculator"). Defaults to "bolt".</summary>
    public string IconName { get; init; } = "bolt";
}
