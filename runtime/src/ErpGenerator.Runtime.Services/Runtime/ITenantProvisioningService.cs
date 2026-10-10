using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface ITenantProvisioningService
{
    Task ProvisionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);
}
