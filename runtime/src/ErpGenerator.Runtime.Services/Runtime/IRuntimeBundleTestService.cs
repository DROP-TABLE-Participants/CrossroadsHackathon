using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeBundleTestService
{
    Task<RuntimeBundleTestRunResult> RunAsync(ResolvedTenantEnvironment tenant, string bundleVersion, BundleSeedDocument? seed, IReadOnlyList<BundleAcceptanceTestDocument> tests, CancellationToken cancellationToken = default);
    Task<RuntimeBundleTestRunResult> RunAsync(ResolvedTenantEnvironment tenant, string bundleVersion, string bundleHash, BundleSeedDocument? seed, IReadOnlyList<BundleAcceptanceTestDocument> tests, CancellationToken cancellationToken = default);
    Task<bool> HasPassingResultAsync(ResolvedTenantEnvironment tenant, string bundleVersion, string bundleHash, string testSuiteHash, CancellationToken cancellationToken = default);
}
