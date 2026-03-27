namespace Thalamus.Core;

/// <summary>
/// A typed value that flows through Synapse connections during graph execution.
/// Implemented as an abstract record with sealed nested cases — the C# equivalent
/// of an F# discriminated union. Each case is a positional record for concise construction.
/// </summary>
public abstract record DataPacket
{
    private protected DataPacket() { }

    /// <summary>Human-readable value string for UI port labels.</summary>
    public abstract string DisplayString { get; }

    public sealed record IntValue(int Value) : DataPacket
    {
        public override string DisplayString => Value.ToString();
    }

    public sealed record FloatValue(float Value) : DataPacket
    {
        public override string DisplayString => Value.ToString("G6");
    }

    public sealed record BoolValue(bool Value) : DataPacket
    {
        public override string DisplayString => Value ? "True" : "False";
    }

    public sealed record StringValue(string Value) : DataPacket
    {
        public override string DisplayString => Value;
    }

    public sealed record UInt32Value(uint Value) : DataPacket
    {
        public override string DisplayString => Value.ToString();
    }
}
