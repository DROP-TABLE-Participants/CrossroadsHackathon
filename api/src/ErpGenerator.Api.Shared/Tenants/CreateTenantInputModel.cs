namespace ErpGenerator.Api.Shared.Tenants;

public sealed record CreateTenantInputModel(
    string? CompanyName = null,
    string? Slug = null,
    string? Plan = null,
    string? EnvironmentName = null,
    string? BundleSourceType = null,
    Guid? BundleRunId = null);
