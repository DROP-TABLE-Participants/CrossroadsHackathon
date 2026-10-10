using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Preparation;

public interface ITenantPreparationDatabaseResetter
{
    Task<TenantPreparationDatabaseResetResult> ResetAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);
}

