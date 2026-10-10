namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminAgentExecutionAttemptViewModel(
    Guid Id,
    int AttemptNumber,
    string Status,
    string AgentKey,
    string AgentVersion,
    string DefinitionHash,
    string InputManifestHash,
    string? OutputArtifactHash,
    string? FailureDisposition,
    string? FailureCode,
    string? FailureMessage,
    string? RepairInstructions,
    int ModelCallCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);
