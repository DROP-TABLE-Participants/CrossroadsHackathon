namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationRunListItemViewModel(
    Guid Id,
    Guid GenerationProjectId,
    Guid CustomerId,
    string CompanyName,
    string CustomerEmail,
    string Status,
    string RequestedMode,
    string? ActualMode,
    string? CurrentStage,
    int ClaimCount,
    int FailureCount,
    string? FailureDisposition,
    string? FailureCode,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
