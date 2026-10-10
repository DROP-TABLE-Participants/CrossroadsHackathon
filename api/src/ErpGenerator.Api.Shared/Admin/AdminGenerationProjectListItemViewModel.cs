namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationProjectListItemViewModel(
    Guid Id,
    Guid CustomerId,
    string CompanyName,
    string CustomerName,
    string CustomerEmail,
    string Status,
    string DiscoveryMode,
    string? LatestRunStatus,
    int PendingWorkCount,
    DateTimeOffset UpdatedAt);
