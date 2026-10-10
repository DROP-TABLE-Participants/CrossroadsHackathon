using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record TenantDatabaseCredentialViewModel(
    string SecretRef,
    string TenantStableId,
    string Environment,
    string DatabaseName,
    string Username,
    string ConnectionString);
