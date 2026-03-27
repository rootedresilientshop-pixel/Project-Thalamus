using System.Diagnostics;

namespace Thalamus.Core;

/// <summary>
/// Executes a GraphState by topologically sorting its nodes using Kahn's algorithm,
/// then invoking each provider's Execute method in dependency order.
/// Static class — no state, purely functional over GraphState.
/// </summary>
public static class GraphExecutor
{
    /// <summary>
    /// Runs the entire graph and returns both results, the topological execution order, and per-node timing.
    /// Results: flat value map for all ports (nodeId, portName, isOutput) → DataPacket.
    /// ExecutionOrder: List of node IDs in Kahn's topological sort order.
    /// Elapsed: total time taken to execute all nodes.
    /// NodeTimes: per-node execution time (only includes nodes in ExecutionOrder).
    /// Nodes in cycles that cannot be sorted are silently skipped.
    /// </summary>
    public static (
        Dictionary<(Guid nodeId, string portName, bool isOutput), DataPacket> Results,
        IReadOnlyList<Guid> ExecutionOrder,
        TimeSpan Elapsed,
        IReadOnlyDictionary<Guid, TimeSpan> NodeTimes
    ) ExecuteWithOrder(
        GraphState graph,
        IEnumerable<INucleusProvider> providers)
    {
        var providerMap = providers.ToDictionary(p => p.ProviderName, StringComparer.Ordinal);

        // Build in-degree and successor tables for Kahn's algorithm
        var inDegree   = new Dictionary<Guid, int>(graph.Nodes.Count);
        var successors = new Dictionary<Guid, List<Guid>>(graph.Nodes.Count);

        foreach (var node in graph.Nodes)
        {
            inDegree[node.Id]   = 0;
            successors[node.Id] = [];
        }

        foreach (var synapse in graph.Synapses)
        {
            inDegree[synapse.InputNodeId]++;
            successors[synapse.OutputNodeId].Add(synapse.InputNodeId);
        }

        // Seed the queue with all root nodes (nothing flows into them)
        var queue = new Queue<Guid>(
            inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));

        // Build a nodeId lookup for O(1) retrieval during sort
        var nodeById = graph.Nodes.ToDictionary(n => n.Id);

        var executionOrder = new List<PlacedNode>(graph.Nodes.Count);
        var executionOrderIds = new List<Guid>(graph.Nodes.Count);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            executionOrder.Add(nodeById[id]);
            executionOrderIds.Add(id);

            foreach (var successorId in successors[id])
            {
                if (--inDegree[successorId] == 0)
                    queue.Enqueue(successorId);
            }
        }

        // Execution: propagate values through the sorted node list
        var nodeOutputs = new Dictionary<Guid, Dictionary<string, DataPacket>>(executionOrder.Count);
        var result      = new Dictionary<(Guid, string, bool), DataPacket>();
        var nodeTimes   = new Dictionary<Guid, TimeSpan>(executionOrder.Count);

        // Pre-build synapse lookup by input node for O(synapses) total
        var synapsesByInputNode = new Dictionary<Guid, List<Synapse>>();
        foreach (var synapse in graph.Synapses)
        {
            if (!synapsesByInputNode.TryGetValue(synapse.InputNodeId, out var list))
                synapsesByInputNode[synapse.InputNodeId] = list = [];
            list.Add(synapse);
        }

        var overallStart = Stopwatch.GetTimestamp();

        foreach (var node in executionOrder)
        {
            // Gather this node's inputs from upstream outputs
            var inputs = new Dictionary<string, DataPacket>(StringComparer.Ordinal);

            if (synapsesByInputNode.TryGetValue(node.Id, out var incomingSynapses))
            {
                foreach (var syn in incomingSynapses)
                {
                    if (!nodeOutputs.TryGetValue(syn.OutputNodeId, out var upstreamOutputs)) continue;
                    if (!upstreamOutputs.TryGetValue(syn.OutputPortName, out var packet))    continue;

                    inputs[syn.InputPortName] = packet;
                    result[(node.Id, syn.InputPortName, false)] = packet;  // record received input
                }
            }

            // Execute via provider — measure per-node time
            var nodeStart = Stopwatch.GetTimestamp();
            var providerName = node.Schema.ProviderName;
            var outputs = providerName != null && providerMap.TryGetValue(providerName, out var provider)
                ? provider.Execute(node.Schema, inputs)
                : [];
            nodeTimes[node.Id] = Stopwatch.GetElapsedTime(nodeStart);

            nodeOutputs[node.Id] = outputs;

            foreach (var (portName, packet) in outputs)
                result[(node.Id, portName, true)] = packet;
        }

        return (result, executionOrderIds.AsReadOnly(), Stopwatch.GetElapsedTime(overallStart), nodeTimes);
    }

    /// <summary>
    /// Convenience wrapper around ExecuteWithOrder that returns only the results.
    /// Delegates to ExecuteWithOrder to avoid code duplication.
    /// </summary>
    public static Dictionary<(Guid nodeId, string portName, bool isOutput), DataPacket> Execute(
        GraphState graph,
        IEnumerable<INucleusProvider> providers)
        => ExecuteWithOrder(graph, providers).Results;
}
