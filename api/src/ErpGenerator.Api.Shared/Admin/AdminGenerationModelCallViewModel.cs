namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationModelCallViewModel(
    Guid Id,
    Guid? WorkItemId,
    Guid? AttemptId,
    string Phase,
    string Status,
    string RequestHash,
    string? ProviderResponseId,
    string? AgentKey,
    string? AgentVersion,
    string? Provider,
    string? Model,
    string? PromptVersion,
    string? ContractVersion,
    long? DurationMilliseconds,
    int? InputTokenCount,
    int? OutputTokenCount,
    string? ResponseHash,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
