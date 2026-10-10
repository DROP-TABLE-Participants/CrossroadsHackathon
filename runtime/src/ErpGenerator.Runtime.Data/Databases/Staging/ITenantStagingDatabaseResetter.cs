using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Staging;

public interface ITenantStagingDatabaseResetter
{
    Task<TenantStagingDatabaseResetResult> ResetAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);
}

