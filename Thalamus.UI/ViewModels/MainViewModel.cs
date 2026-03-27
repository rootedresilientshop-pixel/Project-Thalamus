using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Data;
using Thalamus.Core;

namespace Thalamus.UI.ViewModels;

/// <summary>
/// Drives MainWindow. Holds the provider palette (left panel), the live list of
/// NodeViewModels currently placed on the canvas, and the Synapses (connections).
/// </summary>
public sealed class MainViewModel
{
    private GraphState _graph = new();
    private string _filterText = string.Empty;
    private readonly IReadOnlyList<INucleusProvider> _providers;

    // Phase 13: UDP broadcast for live sync
    private UdpClient? _udpClient;

    // Trial Edition: Project Ceiling
    public const int MAX_TRIAL_NODES = 10;
    public bool IsProVersion { get; set; } = false;
    public event Action? TrialLimitReached;

    /// <summary>All schemas available from all loaded providers — the palette.</summary>
    public IReadOnlyList<NodeSchema> Palette { get; }

    /// <summary>CollectionViewSource for the palette with grouping by Category and filtering.</summary>
    public CollectionViewSource PaletteViewSource { get; }

    /// <summary>Nodes currently on the canvas.</summary>
    public ObservableCollection<NodeViewModel> PlacedNodes { get; } = [];

    /// <summary>Synapses (connections) currently in the graph.</summary>
    public ObservableCollection<SynapseViewModel> Synapses { get; } = [];

    /// <summary>
    /// Filter text for the palette. Setting this triggers a refresh of the view.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            _filterText = value ?? string.Empty;
            PaletteViewSource.View.Refresh();
        }
    }

    public MainViewModel(IEnumerable<INucleusProvider> providers)
    {
        // Materialize providers list to avoid double-enumeration
        var providerList = providers.ToList();
        _providers = providerList.AsReadOnly();

        // Build palette, tagging each schema with its provider name for later persistence
        Palette = providerList
            .SelectMany(p => p.GetSchemas().Select(s => s with { ProviderName = p.ProviderName }))
            .ToList()
            .AsReadOnly();

        // Create the CollectionViewSource with category grouping
        PaletteViewSource = new CollectionViewSource { Source = Palette };
        PaletteViewSource.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(NodeSchema.Category)));
        PaletteViewSource.Filter += OnPaletteFilter;
    }

    /// <summary>
    /// Filter handler — accepts items that match the filter text (Name or Description).
    /// </summary>
    private void OnPaletteFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not NodeSchema schema)
        {
            e.Accepted = false;
            return;
        }

        string query = _filterText.Trim();
        if (string.IsNullOrEmpty(query))
        {
            e.Accepted = true;
            return;
        }

        // Match Name or Description (case-insensitive)
        e.Accepted = schema.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                  || (schema.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Called by the UI when the user double-clicks a palette item or drops a schema
    /// onto the canvas. Adds the node to GraphState and creates a NodeViewModel.
    /// Trial Edition: Returns false and fires TrialLimitReached if the 10-node limit is exceeded.
    /// </summary>
    public bool PlaceNode(NodeSchema schema, double x, double y)
    {
        if (!IsProVersion && PlacedNodes.Count >= MAX_TRIAL_NODES)
        {
            TrialLimitReached?.Invoke();
            return false;
        }
        var placed = _graph.AddNode(schema, x, y);
        PlacedNodes.Add(new NodeViewModel(placed));
        return true;
    }

    /// <summary>
    /// Validates types, creates the Synapse in GraphState, and adds a SynapseViewModel.
    /// Returns the new SynapseViewModel, or null if invalid (type mismatch or self-loop).
    /// </summary>
    public SynapseViewModel? ConnectPorts(
        Guid   outputNodeId, string outputPortName, string outputPortType,
        Guid   inputNodeId,  string inputPortName,  string inputPortType)
    {
        // Type safety: "any" is a wildcard on either side
        bool typesCompatible =
            outputPortType == "any" ||
            inputPortType  == "any" ||
            outputPortType == inputPortType;

        if (!typesCompatible) return null;

        var synapse = _graph.AddSynapse(outputNodeId, outputPortName,
                                        inputNodeId,  inputPortName);
        if (synapse is null) return null;  // self-loop rejected by GraphState

        var vm = new SynapseViewModel(synapse);
        Synapses.Add(vm);
        return vm;
    }

    /// <summary>
    /// Removes a Synapse from both the model and the UI collection.
    /// </summary>
    public bool DisconnectSynapse(Guid synapseId)
    {
        var vm = Synapses.FirstOrDefault(s => s.Id == synapseId);
        if (vm is null) return false;
        Synapses.Remove(vm);
        return _graph.RemoveSynapse(synapseId);
    }

    /// <summary>
    /// Clears all nodes and synapses (used when loading a new graph or on New).
    /// </summary>
    public void ClearAll()
    {
        PlacedNodes.Clear();
        Synapses.Clear();
        _graph = new GraphState();
    }

    /// <summary>
    /// Adds a node with a specific ID (used during load to preserve synapse references).
    /// </summary>
    public void PlaceNodeWithId(Guid id, NodeSchema schema, double x, double y)
    {
        var placed = _graph.AddNodeWithId(id, schema, x, y);
        PlacedNodes.Add(new NodeViewModel(placed));
    }

    /// <summary>
    /// Adds a synapse with a specific ID (used during load). Type-checks first.
    /// Returns the new SynapseViewModel, or null if invalid.
    /// </summary>
    public SynapseViewModel? ConnectPortsWithId(
        Guid   synapseId,
        Guid   outputNodeId, string outputPortName, string outputPortType,
        Guid   inputNodeId,  string inputPortName,  string inputPortType)
    {
        // Type safety: "any" is a wildcard on either side
        bool typesCompatible =
            outputPortType == "any" ||
            inputPortType  == "any" ||
            outputPortType == inputPortType;

        if (!typesCompatible) return null;

        var synapse = _graph.AddSynapseWithId(synapseId, outputNodeId, outputPortName,
                                              inputNodeId,  inputPortName);
        if (synapse is null) return null;  // self-loop rejected by GraphState

        var vm = new SynapseViewModel(synapse);
        Synapses.Add(vm);
        return vm;
    }

    /// <summary>
    /// Executes the current graph and returns a flat port-value map.
    /// The key (nodeId, portName, isOutput) matches MainWindow._portDotMap,
    /// allowing O(1) UI lookup per port.
    /// </summary>
    public Dictionary<(Guid, string, bool), DataPacket> Pulse()
        => GraphExecutor.Execute(_graph, _providers);

    /// <summary>
    /// Executes the current graph and returns results, execution order, and timing info.
    /// ExecutionOrder is a list of node IDs in the order they are executed.
    /// Elapsed is the total execution time; NodeTimes contains per-node timings.
    /// Used by the UI for staggered animation effects (Phase 11) and timing display (Phase 12).
    /// </summary>
    public (Dictionary<(Guid, string, bool), DataPacket> Results,
            IReadOnlyList<Guid> ExecutionOrder,
            TimeSpan Elapsed,
            IReadOnlyDictionary<Guid, TimeSpan> NodeTimes)
        PulseWithOrder()
        => GraphExecutor.ExecuteWithOrder(_graph, _providers);

    /// <summary>
    /// Transpiles the current graph to a C# class string.
    /// Returns production-ready code that can be saved and compiled.
    /// </summary>
    public string ExportCSharp()
        => CSharpTranspiler.Transpile(_graph, _providers);

    /// <summary>
    /// Phase 13: Broadcasts output port values as JSON to localhost:9000 for live game engine sync.
    /// Key format: "NodeName.PortName" → DisplayString value.
    /// Silently ignores network errors (UDP is best-effort).
    /// </summary>
    public void BroadcastResults(Dictionary<(Guid, string, bool), DataPacket> results)
    {
        try
        {
            _udpClient ??= new UdpClient();
            var nodeNames = _graph.Nodes.ToDictionary(n => n.Id, n => n.Schema.Name);
            var payload   = new Dictionary<string, string>();
            foreach (var ((nodeId, portName, isOutput), packet) in results)
            {
                if (!isOutput) continue;
                string key = nodeNames.TryGetValue(nodeId, out var n) ? $"{n}.{portName}" : portName;
                payload[key] = packet.DisplayString;
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            _udpClient.Send(bytes, bytes.Length, "127.0.0.1", 9000);
        }
        catch { /* UDP is best-effort; silently ignore all network errors */ }
    }
}
