namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationStageViewModel(
    Guid Id,
    string StageKey,
    string Status,
    int Sequence,
    int AttemptCount,
    string? InputHash,
    string? Summary,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);
