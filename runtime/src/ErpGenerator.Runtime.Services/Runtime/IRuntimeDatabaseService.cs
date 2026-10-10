using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeDatabaseService
{
    Task<RuntimeUser?> ConsumeGuestEntryAsync(ResolvedTenantEnvironment tenant, string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<RuntimeMetadata> ReadMetadataAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeBundleDeploymentHistoryItem>> ReadDeploymentHistoryAsync(
        ResolvedTenantEnvironment tenant,
        int limit,
        CancellationToken cancellationToken = default);

    Task<CompiledBundle?> ReadActiveCompiledBundleAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);

    Task<CompiledBundle?> ReadCompiledBundleByVersionAsync(
        ResolvedTenantEnvironment tenant,
        string bundleVersion,
        CancellationToken cancellationToken = default);

    Task<RuntimeBundleDeploymentResult> ApplyBundleDeploymentAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle compiled,
        BundleMigrationPlan plan,
        string environment,
        string status,
        bool activate,
        string message,
        CancellationToken cancellationToken = default);

    Task<RuntimeRollbackResult> RollbackActiveBundleAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);

    Task<string> CreateActivationTokenAsync(
        ResolvedTenantEnvironment tenant,
        string email,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task<bool> ActivateOwnerAsync(
        ResolvedTenantEnvironment tenant,
        string token,
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken = default);

    Task<RuntimeUser?> ValidateLoginAsync(
        ResolvedTenantEnvironment tenant,
        string email,
        string password,
        CancellationToken cancellationToken = default);
}
