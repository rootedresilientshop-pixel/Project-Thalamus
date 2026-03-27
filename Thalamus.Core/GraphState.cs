namespace Thalamus.Core;

/// <summary>
/// Runtime state of one graph — the collection of node instances placed on the canvas
/// and the synapses (connections) between them. This is the document model: serialize
/// this to save/load a graph.
/// </summary>
public sealed class GraphState
{
    private readonly List<PlacedNode> _nodes    = [];
    private readonly List<Synapse>    _synapses = [];

    public IReadOnlyList<PlacedNode> Nodes    => _nodes;
    public IReadOnlyList<Synapse>    Synapses => _synapses;

    // ── Node management ────────────────────────────────────────────
    public PlacedNode AddNode(NodeSchema schema, double x, double y)
    {
        var node = new PlacedNode(Guid.NewGuid(), schema, x, y);
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Adds a node with a specific ID (used during load to preserve synapse references).
    /// </summary>
    public PlacedNode AddNodeWithId(Guid id, NodeSchema schema, double x, double y)
    {
        var node = new PlacedNode(id, schema, x, y);
        _nodes.Add(node);
        return node;
    }

    public bool RemoveNode(Guid id)
    {
        var node = _nodes.FirstOrDefault(n => n.Id == id);
        if (node is null) return false;
        _nodes.Remove(node);
        // Cascade-remove any synapses that reference this node
        _synapses.RemoveAll(s => s.OutputNodeId == id || s.InputNodeId == id);
        return true;
    }

    // ── Synapse management ────────────────────────────────────────
    /// <summary>
    /// Creates and stores a Synapse. Returns null if the connection is invalid
    /// (self-loop on the same node). The caller is responsible for type checking
    /// before calling this method.
    /// </summary>
    public Synapse? AddSynapse(
        Guid   outputNodeId, string outputPortName,
        Guid   inputNodeId,  string inputPortName)
    {
        // Self-loop guard: same node cannot connect to itself
        if (outputNodeId == inputNodeId) return null;

        var synapse = new Synapse(
            Guid.NewGuid(),
            outputNodeId, outputPortName,
            inputNodeId,  inputPortName);

        _synapses.Add(synapse);
        return synapse;
    }

    /// <summary>
    /// Adds a synapse with a specific ID (used during load to preserve synapse identity).
    /// Returns null if the connection is invalid (self-loop).
    /// </summary>
    public Synapse? AddSynapseWithId(
        Guid   synapseId,
        Guid   outputNodeId, string outputPortName,
        Guid   inputNodeId,  string inputPortName)
    {
        // Self-loop guard: same node cannot connect to itself
        if (outputNodeId == inputNodeId) return null;

        var synapse = new Synapse(synapseId, outputNodeId, outputPortName, inputNodeId, inputPortName);
        _synapses.Add(synapse);
        return synapse;
    }

    public bool RemoveSynapse(Guid id)
    {
        var s = _synapses.FirstOrDefault(x => x.Id == id);
        if (s is null) return false;
        _synapses.Remove(s);
        return true;
    }
}

/// <summary>
/// A specific instance of a NodeSchema placed at a position on the canvas.
/// </summary>
public sealed record PlacedNode(Guid Id, NodeSchema Schema, double X, double Y);
