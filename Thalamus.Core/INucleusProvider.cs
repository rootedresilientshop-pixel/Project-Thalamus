namespace Thalamus.Core;

/// <summary>
/// Implemented by any assembly that wants to register node schemas with Thalamus.
/// The UI discovers providers at startup and calls GetSchemas() to build the palette.
/// </summary>
public interface INucleusProvider
{
    /// <summary>Human-readable name of this provider, e.g. "BridgeMod".</summary>
    string ProviderName { get; }

    /// <summary>Returns all NodeSchemas this provider declares.</summary>
    IReadOnlyList<NodeSchema> GetSchemas();

    /// <summary>
    /// Executes a single node of this provider's type and returns its output values.
    /// The default implementation returns an empty dictionary, so providers that do
    /// not yet implement execution degrade gracefully rather than failing.
    /// </summary>
    /// <param name="schema">The NodeSchema identifying which node type to run.</param>
    /// <param name="inputs">Port name → DataPacket for each connected input.</param>
    /// <returns>Port name → DataPacket for each output produced.</returns>
    Dictionary<string, DataPacket> Execute(
        NodeSchema schema,
        Dictionary<string, DataPacket> inputs)
        => [];

    /// <summary>
    /// Returns a C# code snippet template for this node type (used by the transpiler).
    /// The template uses {PortName} placeholders that the transpiler replaces with
    /// actual variable names. Return null if this node has no transpiler implementation.
    /// The default implementation returns null, so providers that do not implement
    /// code generation degrade gracefully.
    /// </summary>
    /// <param name="schema">The NodeSchema identifying which node type to transpile.</param>
    /// <param name="inputVars">Port name → C# variable name/expression for connected inputs,
    /// or default literal for unconnected inputs.</param>
    /// <param name="outputVars">Port name → C# variable name to assign outputs to.</param>
    /// <returns>C# code template with {portName} placeholders, or null if not implemented.</returns>
    string? GetCodeSnippet(
        NodeSchema schema,
        IReadOnlyDictionary<string, string> inputVars,
        IReadOnlyDictionary<string, string> outputVars)
        => null;
}
