using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record StoreTenantDatabaseCredentialInputModel(
    string TenantStableId,
    string Environment,
    string DatabaseName,
    string Username,
    string Host,
    int Port,
    string Database,
    string Password,
    string SslMode);
