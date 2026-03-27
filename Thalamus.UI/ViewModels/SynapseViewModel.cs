using Thalamus.Core;

namespace Thalamus.UI.ViewModels;

/// <summary>
/// Thin UI wrapper around a Synapse record. Carries only what the
/// code-behind needs to look up and refresh wire Path elements.
/// No INotifyPropertyChanged — positions are recalculated imperatively.
/// </summary>
public sealed class SynapseViewModel
{
    public Guid   Id             { get; }
    public Guid   OutputNodeId   { get; }
    public string OutputPortName { get; }
    public Guid   InputNodeId    { get; }
    public string InputPortName  { get; }

    public SynapseViewModel(Synapse synapse)
    {
        Id             = synapse.Id;
        OutputNodeId   = synapse.OutputNodeId;
        OutputPortName = synapse.OutputPortName;
        InputNodeId    = synapse.InputNodeId;
        InputPortName  = synapse.InputPortName;
    }
}
