using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public interface ITenantDatabaseConnectionFactory
{
    string CreateConnectionString(ResolvedTenantEnvironment tenant);

    string CreateConnectionString(ResolvedTenantEnvironment tenant, TenantDatabaseConnectionPurpose purpose);
}
