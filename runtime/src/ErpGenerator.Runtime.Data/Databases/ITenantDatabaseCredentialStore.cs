using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public interface ITenantDatabaseCredentialStore
{
    void Store(TenantDatabaseCredential credential);

    bool TryGet(ResolvedTenantEnvironment tenant, out TenantDatabaseCredential credential);
}
