using ErpGenerator.Runtime.Shared.Tenants;
using Npgsql;

namespace ErpGenerator.Runtime.Data.Databases;

public interface ITenantDatabaseProvisioner
{
    Task ProvisionAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default);

    Task GrantRuntimePrivilegesAsync(
        ResolvedTenantEnvironment tenant,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default);
}
