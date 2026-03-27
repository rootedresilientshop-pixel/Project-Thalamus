using Thalamus.Core;

namespace Thalamus.UI.ViewModels;

/// <summary>
/// Bindable wrapper around a PlacedNode for the Canvas.
/// X and Y are the canvas position of the node card's top-left corner.
/// </summary>
public sealed class NodeViewModel
{
    public Guid      Id          { get; }
    public string    Name        { get; }
    public string    Category    { get; }
    public string?   Description { get; }
    public string?   ProviderName { get; }
    public string    IconName    { get; }
    public double    X           { get; set; }
    public double    Y           { get; set; }

    public IReadOnlyList<PortSchema> Inputs  { get; }
    public IReadOnlyList<PortSchema> Outputs { get; }

    /// <summary>The original NodeSchema (Phase 14: used for documentation popups).</summary>
    public NodeSchema Schema { get; }

    public NodeViewModel(PlacedNode placed)
    {
        Id          = placed.Id;
        Name        = placed.Schema.Name;
        Category    = placed.Schema.Category;
        Description = placed.Schema.Description;
        ProviderName = placed.Schema.ProviderName;
        IconName    = placed.Schema.IconName;
        X           = placed.X;
        Y           = placed.Y;
        Inputs      = placed.Schema.Inputs;
        Outputs     = placed.Schema.Outputs;
        Schema      = placed.Schema;
    }
}
