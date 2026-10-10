using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeTenantEnvironmentViewModel(
    string TenantId,
    string TenantStableId,
    string TenantSlug,
    string Host,
    string Environment,
    string DatabaseName,
    string? DatabaseConnectionString,
    string RuntimeBaseUrl,
    string DatabaseCredentialMode,
    string? DatabaseCredentialSecretRef);
