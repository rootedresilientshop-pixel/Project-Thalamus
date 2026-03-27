using System.Text.Json.Serialization;

namespace Thalamus.Core;

/// <summary>
/// Declares a single input or output port on a NodeSchema.
/// </summary>
public sealed record PortSchema
{
    /// <summary>Display name of the port, e.g. "Seed", "Value".</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Logical type tag — freeform string, interpreted by the provider.
    /// E.g. "float", "uint32", "bool". The UI uses this for port color coding.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "any";
}
