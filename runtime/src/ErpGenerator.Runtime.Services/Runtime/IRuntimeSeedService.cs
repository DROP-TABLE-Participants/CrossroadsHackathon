using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeSeedService
{
    Task<RuntimeSeedResult> ApplySeedAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, BundleSeedDocument seed, CancellationToken cancellationToken = default);
}
