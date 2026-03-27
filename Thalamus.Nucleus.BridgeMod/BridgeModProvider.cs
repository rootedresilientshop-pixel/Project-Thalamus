using Thalamus.Core;

namespace Thalamus.Nucleus.BridgeMod;

/// <summary>
/// Registers the BridgeMod node palette with Thalamus.
/// This is the single entry point the UI discovers via reflection or direct instantiation.
/// </summary>
public sealed class BridgeModProvider : INucleusProvider
{
    public string ProviderName => "BridgeMod";

    public IReadOnlyList<NodeSchema> GetSchemas() =>
    [
        new NodeSchema
        {
            Name        = "Xorshift32",
            Description = "Generates a pseudo-random uint32 via xorshift algorithm.",
            Category    = "Math",
            IconName    = "calculator",
            Inputs      =
            [
                new PortSchema { Name = "Seed",    Type = "uint32" },
                new PortSchema { Name = "Enabled", Type = "bool"   }
            ],
            Outputs     = [ new PortSchema { Name = "Value", Type = "uint32" } ]
        },
        new NodeSchema
        {
            Name        = "WeightClamping",
            Description = "Clamps a weight value to [min, max] range.",
            Category    = "Math",
            IconName    = "calculator",
            Inputs      =
            [
                new PortSchema { Name = "Weight",  Type = "float" },
                new PortSchema { Name = "Min",     Type = "float" },
                new PortSchema { Name = "Max",     Type = "float" },
                new PortSchema { Name = "Enabled", Type = "bool"  }
            ],
            Outputs     =
            [
                new PortSchema { Name = "Clamped",    Type = "float" },
                new PortSchema { Name = "WasClamped", Type = "bool"  }
            ]
        }
    ];

    public Dictionary<string, DataPacket> Execute(
        NodeSchema schema,
        Dictionary<string, DataPacket> inputs)
    {
        return schema.Name switch
        {
            "Xorshift32"     => ExecuteXorshift32(inputs),
            "WeightClamping" => ExecuteWeightClamping(inputs),
            _                => []
        };
    }

    public string? GetCodeSnippet(
        NodeSchema schema,
        IReadOnlyDictionary<string, string> inputVars,
        IReadOnlyDictionary<string, string> outputVars)
    {
        return schema.Name switch
        {
            "Xorshift32"     => GetXorshift32Snippet(),
            "WeightClamping" => GetWeightClampingSnippet(),
            _                => null
        };
    }

    private static Dictionary<string, DataPacket> ExecuteXorshift32(
        Dictionary<string, DataPacket> inputs)
    {
        // Extract Enabled — default true
        bool enabled = true;
        if (inputs.TryGetValue("Enabled", out var enabledPacket) &&
            enabledPacket is DataPacket.BoolValue bv)
        {
            enabled = bv.Value;
        }

        if (!enabled) return [];

        // Extract Seed — default 1
        uint seed = 1;
        if (inputs.TryGetValue("Seed", out var seedPacket) &&
            seedPacket is DataPacket.UInt32Value uv)
        {
            seed = uv.Value;
        }
        if (seed == 0) seed = 1;  // xorshift32 is undefined for state=0

        // Xorshift32 algorithm
        uint state = seed;
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;

        return new Dictionary<string, DataPacket>
        {
            ["Value"] = new DataPacket.UInt32Value(state)
        };
    }

    private static Dictionary<string, DataPacket> ExecuteWeightClamping(
        Dictionary<string, DataPacket> inputs)
    {
        // Extract Enabled — default true
        bool enabled = true;
        if (inputs.TryGetValue("Enabled", out var enabledPacket) &&
            enabledPacket is DataPacket.BoolValue bv)
        {
            enabled = bv.Value;
        }

        if (!enabled) return [];

        // Extract Weight — default 0.0f
        float weight = 0f;
        if (inputs.TryGetValue("Weight", out var weightPacket) &&
            weightPacket is DataPacket.FloatValue wv)
        {
            weight = wv.Value;
        }

        // Extract Min — default 0.0f
        float min = 0f;
        if (inputs.TryGetValue("Min", out var minPacket) &&
            minPacket is DataPacket.FloatValue minV)
        {
            min = minV.Value;
        }

        // Extract Max — default 1.0f
        float max = 1f;
        if (inputs.TryGetValue("Max", out var maxPacket) &&
            maxPacket is DataPacket.FloatValue maxV)
        {
            max = maxV.Value;
        }

        float clamped = Math.Clamp(weight, min, max);
        bool wasClamped = clamped != weight;

        return new Dictionary<string, DataPacket>
        {
            ["Clamped"]    = new DataPacket.FloatValue(clamped),
            ["WasClamped"] = new DataPacket.BoolValue(wasClamped)
        };
    }

    private static string GetXorshift32Snippet() =>
        @"uint {Value} = 0u;
if ({Enabled})
{
    {Value} = {Seed} == 0u ? 1u : {Seed};
    {Value} ^= {Value} << 13;
    {Value} ^= {Value} >> 17;
    {Value} ^= {Value} << 5;
}";

    private static string GetWeightClampingSnippet() =>
        @"float {Clamped} = 0f;
bool {WasClamped} = false;
if ({Enabled})
{
    {Clamped} = Math.Clamp({Weight}, {Min}, {Max});
    {WasClamped} = {Clamped} != {Weight};
}";
}
