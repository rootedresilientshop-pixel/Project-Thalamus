using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thalamus.Core;

/// <summary>
/// Represents a persisted graph state — nodes, synapses, metadata, and schema version.
/// Serializable to/from .thalamus JSON files.
/// </summary>
public sealed class GraphSchema
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = [];

    [JsonPropertyName("nodes")]
    public List<SavedNode> Nodes { get; set; } = [];

    [JsonPropertyName("synapses")]
    public List<SavedSynapse> Synapses { get; set; } = [];

    /// <summary>
    /// Serializes the graph schema to a .thalamus JSON file.
    /// </summary>
    public static void Save(GraphSchema schema, string path)
    {
        string json = JsonSerializer.Serialize(schema, _jsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Deserializes a .thalamus JSON file to a graph schema.
    /// Returns null if the file does not exist or is invalid JSON.
    /// </summary>
    public static GraphSchema? Load(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GraphSchema>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// A node instance in a persisted graph, identified by provider name + schema name.
/// </summary>
public sealed class SavedNode
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("providerName")]
    public required string ProviderName { get; set; }

    [JsonPropertyName("schemaName")]
    public required string SchemaName { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

/// <summary>
/// A connection in a persisted graph, mirroring the Synapse record.
/// </summary>
public sealed class SavedSynapse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("outputNodeId")]
    public Guid OutputNodeId { get; set; }

    [JsonPropertyName("outputPortName")]
    public required string OutputPortName { get; set; }

    [JsonPropertyName("inputNodeId")]
    public Guid InputNodeId { get; set; }

    [JsonPropertyName("inputPortName")]
    public required string InputPortName { get; set; }
}
