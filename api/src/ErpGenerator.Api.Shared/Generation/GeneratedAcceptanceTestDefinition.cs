namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedAcceptanceTestDefinition(
    string Key,
    string Label,
    string Kind,
    string? EntityKey,
    string? ReportKey,
    IReadOnlyList<GeneratedAcceptanceValueDefinition> Values);
