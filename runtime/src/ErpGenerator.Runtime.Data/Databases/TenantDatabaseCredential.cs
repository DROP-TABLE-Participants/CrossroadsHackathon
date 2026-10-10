using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed record TenantDatabaseCredential(
    string TenantStableId,
    TenantEnvironmentKind Environment,
    string DatabaseName,
    string UserName,
    string ConnectionString,
    string? SecretReference = null);

public sealed record TenantDatabaseCredentialDescriptor(
    string SecretReference,
    string TenantStableId,
    TenantEnvironmentKind Environment,
    string DatabaseName,
    string UserName,
    string Host,
    int Port,
    string Database,
    string? SslMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RotatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record TenantDatabaseCredentialSecret(
    TenantDatabaseCredentialDescriptor Descriptor,
    string Password,
    string ConnectionString);
