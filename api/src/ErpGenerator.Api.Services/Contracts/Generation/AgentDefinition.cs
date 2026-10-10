using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentDefinition(
    string Key,
    string Version,
    string Role,
    string Layer,
    string? StageKey,
    string InputArtifactType,
    string OutputArtifactType,
    string OutputSchemaName,
    string OutputSchemaVersion,
    string PromptVersion,
    string? SupervisorAgentKey,
    IReadOnlyList<string> DependsOnAgentKeys,
    IReadOnlyList<string> AllowedRouteAgentKeys,
    int MaximumAttempts,
    int MaximumRepairs,
    int MaximumRoutingDepth,
    bool IsSupervisor)
{
    public string DefinitionHash => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Key,
            Version,
            Role,
            Layer,
            StageKey,
            InputArtifactType,
            OutputArtifactType,
            OutputSchemaName,
            OutputSchemaVersion,
            PromptVersion,
            SupervisorAgentKey,
            DependsOnAgentKeys,
            AllowedRouteAgentKeys,
            MaximumAttempts,
            MaximumRepairs,
            MaximumRoutingDepth,
            IsSupervisor
        }))));
}
