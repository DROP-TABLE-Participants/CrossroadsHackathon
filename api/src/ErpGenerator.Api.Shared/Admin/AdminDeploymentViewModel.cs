namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminDeploymentViewModel(
    Guid Id,
    string Environment,
    string Status,
    string? RuntimeUrl,
    string? TestsStatus,
    int AttemptCount,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset UpdatedAt);
