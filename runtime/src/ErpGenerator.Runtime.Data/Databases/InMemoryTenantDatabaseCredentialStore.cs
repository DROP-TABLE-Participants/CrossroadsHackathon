using System.Collections.Concurrent;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed class InMemoryTenantDatabaseCredentialStore : ITenantDatabaseCredentialStore
{
    private readonly ConcurrentDictionary<string, TenantDatabaseCredential> credentials = new(StringComparer.Ordinal);

    public void Store(TenantDatabaseCredential credential) =>
        credentials[BuildKey(credential.TenantStableId, credential.Environment, credential.DatabaseName)] = credential;

    public bool TryGet(ResolvedTenantEnvironment tenant, out TenantDatabaseCredential credential) =>
        credentials.TryGetValue(BuildKey(tenant.TenantStableId, tenant.Environment, tenant.DatabaseName), out credential!);

    private static string BuildKey(string tenantStableId, TenantEnvironmentKind environment, string databaseName) =>
        $"{tenantStableId}:{environment}:{databaseName}";
}
