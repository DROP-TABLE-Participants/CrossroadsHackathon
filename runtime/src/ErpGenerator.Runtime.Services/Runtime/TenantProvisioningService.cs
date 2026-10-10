using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed class TenantProvisioningService(ITenantDatabaseProvisioner provisioner)
    : ITenantProvisioningService
{
    public Task ProvisionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default) =>
        provisioner.ProvisionAsync(tenant, cancellationToken);
}
