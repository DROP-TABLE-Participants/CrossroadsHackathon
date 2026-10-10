using System.Text.Json;

namespace ErpGenerator.Runtime.Shared.ArtificialIntelligence;

public sealed record RuntimeToolDefinition(
    string Key,
    string Label,
    string Description,
    string Category,
    string Mode,
    string Risk,
    string ResourceType,
    string ResourceKey,
    IReadOnlyList<RuntimeToolFieldDefinition> Input,
    IReadOnlyList<RuntimeToolFieldDefinition> Output,
    IReadOnlyList<RuntimeToolPermissionRequirement> Permissions,
    IReadOnlyList<RuntimeToolEffect> Effects)
{
    public bool Enabled { get; init; }

    public string BundleHash { get; init; } = string.Empty;
}

public sealed record RuntimeToolFieldDefinition(
    string Name,
    string Type,
    bool Required,
    string Description);

public sealed record RuntimeToolPermissionRequirement(
    string Scope,
    string Action,
    string Entity,
    string Target = "");

public sealed record RuntimeToolEffect(
    string Action,
    string Entity,
    string Target = "");

public sealed record RuntimeToolInvocation(
    string ToolKey,
    IReadOnlyDictionary<string, JsonElement> Arguments,
    string RequestId);

public sealed record RuntimeToolResult(
    string ToolKey,
    string RequestId,
    string Status,
    JsonElement? Value,
    IReadOnlyList<RuntimeEvidenceReference> Evidence,
    string BundleHash);

public static class RuntimeToolModes
{
    public const string Read = "read";
    public const string Prepare = "prepare";
}

public static class RuntimeToolRisks
{
    public const string ReadOnly = "read_only";
    public const string Low = "low";
    public const string Consequential = "consequential";
}
