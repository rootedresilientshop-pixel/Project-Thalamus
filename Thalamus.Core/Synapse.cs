namespace Thalamus.Core;

/// <summary>
/// Represents a directed connection between one output port on one node
/// and one input port on another node. Immutable after creation.
/// </summary>
public sealed record Synapse(
    Guid   Id,
    Guid   OutputNodeId,
    string OutputPortName,
    Guid   InputNodeId,
    string InputPortName);
