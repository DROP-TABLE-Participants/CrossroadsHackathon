namespace ErpGenerator.Api.Shared.Tenants;

public sealed record TenantViewModel(
    Guid Id,
    string StableId,
    string Slug,
    string CompanyName,
    string Plan,
    string Status,
    string SetupStatus,
    string? Environment,
    string? Host,
    string? RuntimeUrl,
    string? DeploymentStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
