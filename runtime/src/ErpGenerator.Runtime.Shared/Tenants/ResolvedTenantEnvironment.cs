namespace ErpGenerator.Runtime.Shared.Tenants;

public sealed record ResolvedTenantEnvironment(
    string TenantId,
    string TenantStableId,
    string TenantSlug,
    string Host,
    TenantEnvironmentKind Environment,
    string DatabaseName,
    string? DatabaseConnectionString,
    string RuntimeBaseUrl,
    string? DatabaseCredentialMode = null,
    string? DatabaseCredentialSecretRef = null,
    string Status = "pending");
