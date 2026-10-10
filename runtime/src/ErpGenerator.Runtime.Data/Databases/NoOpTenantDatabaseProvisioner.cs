using ErpGenerator.Runtime.Shared.Tenants;
using Npgsql;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed class NoOpTenantDatabaseProvisioner : ITenantDatabaseProvisioner
{
    public Task ProvisionAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task GrantRuntimePrivilegesAsync(
        ResolvedTenantEnvironment tenant,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
