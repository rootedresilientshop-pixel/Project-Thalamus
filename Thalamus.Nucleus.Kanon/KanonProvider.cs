using Thalamus.Core;

namespace Thalamus.Nucleus.Kanon;

/// <summary>
/// Registers the Kanon node palette with Thalamus.
/// Kanon provides security and governance nodes.
/// </summary>
public sealed class KanonProvider : INucleusProvider
{
    public string ProviderName => "Kanon";

    public IReadOnlyList<NodeSchema> GetSchemas() =>
    [
        new NodeSchema
        {
            Name        = "Identity Sentry",
            Description = "Verifies the cryptographic signature of a mod payload.",
            Category    = "Security",
            IconName    = "shield",
            Inputs      = [ new PortSchema { Name = "Signature", Type = "string" } ],
            Outputs     =
            [
                new PortSchema { Name = "IsAuthorized", Type = "bool"   },
                new PortSchema { Name = "IdentityHash",  Type = "string" }
            ]
        },
        new NodeSchema
        {
            Name        = "Governance Check",
            Description = "Checks if the user level is allowed to perform the action.",
            Category    = "Security",
            IconName    = "shield",
            Inputs      =
            [
                new PortSchema { Name = "Action",    Type = "string" },
                new PortSchema { Name = "UserLevel", Type = "int"    }
            ],
            Outputs     = [ new PortSchema { Name = "Permitted", Type = "bool" } ]
        }
    ];

    public Dictionary<string, DataPacket> Execute(
        NodeSchema schema,
        Dictionary<string, DataPacket> inputs)
    {
        return schema.Name switch
        {
            "Identity Sentry"  => ExecuteIdentitySentry(inputs),
            "Governance Check" => ExecuteGovernanceCheck(inputs),
            _                  => []
        };
    }

    public string? GetCodeSnippet(
        NodeSchema schema,
        IReadOnlyDictionary<string, string> inputVars,
        IReadOnlyDictionary<string, string> outputVars)
    {
        return schema.Name switch
        {
            "Identity Sentry"  => GetIdentitySentrySnippet(),
            "Governance Check" => GetGovernanceCheckSnippet(),
            _                  => null
        };
    }

    private static Dictionary<string, DataPacket> ExecuteIdentitySentry(
        Dictionary<string, DataPacket> inputs)
    {
        string signature = string.Empty;
        if (inputs.TryGetValue("Signature", out var sigPacket) &&
            sigPacket is DataPacket.StringValue sv)
        {
            signature = sv.Value;
        }

        bool isAuthorized = !string.IsNullOrEmpty(signature);
        string identityHash = isAuthorized
            ? (signature.Length >= 8 ? signature[..8] : signature)
            : "ANON";

        return new Dictionary<string, DataPacket>
        {
            ["IsAuthorized"] = new DataPacket.BoolValue(isAuthorized),
            ["IdentityHash"] = new DataPacket.StringValue(identityHash)
        };
    }

    private static Dictionary<string, DataPacket> ExecuteGovernanceCheck(
        Dictionary<string, DataPacket> inputs)
    {
        int userLevel = 0;
        if (inputs.TryGetValue("UserLevel", out var levelPacket) &&
            levelPacket is DataPacket.IntValue iv)
        {
            userLevel = iv.Value;
        }

        bool permitted = userLevel >= 2;

        return new Dictionary<string, DataPacket>
        {
            ["Permitted"] = new DataPacket.BoolValue(permitted)
        };
    }

    private static string GetIdentitySentrySnippet() =>
        @"bool {IsAuthorized} = !string.IsNullOrEmpty({Signature});
string {IdentityHash} = {IsAuthorized}
    ? ({Signature}.Length >= 8 ? {Signature}[..8] : {Signature})
    : ""ANON"";";

    private static string GetGovernanceCheckSnippet() =>
        @"bool {Permitted} = {UserLevel} >= 2;";
}
