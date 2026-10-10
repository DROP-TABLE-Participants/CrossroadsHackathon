using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed class RuntimeDatabaseService(RuntimeDatabase database) : IRuntimeDatabaseService
{
    public Task<RuntimeUser?> ConsumeGuestEntryAsync(ResolvedTenantEnvironment tenant, string token, CancellationToken cancellationToken = default) => database.ConsumeGuestEntryAsync(tenant, token, cancellationToken);
    public Task<RuntimeMetadata> ReadMetadataAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default) =>
        database.ReadMetadataAsync(tenant, cancellationToken);

    public Task<IReadOnlyList<RuntimeBundleDeploymentHistoryItem>> ReadDeploymentHistoryAsync(
        ResolvedTenantEnvironment tenant,
        int limit,
        CancellationToken cancellationToken = default) =>
        database.ReadDeploymentHistoryAsync(tenant, limit, cancellationToken);

    public Task<CompiledBundle?> ReadActiveCompiledBundleAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default) =>
        database.ReadActiveCompiledBundleAsync(tenant, cancellationToken);

    public Task<CompiledBundle?> ReadCompiledBundleByVersionAsync(
        ResolvedTenantEnvironment tenant,
        string bundleVersion,
        CancellationToken cancellationToken = default) =>
        database.ReadCompiledBundleByVersionAsync(tenant, bundleVersion, cancellationToken);

    public Task<RuntimeBundleDeploymentResult> ApplyBundleDeploymentAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle compiled,
        BundleMigrationPlan plan,
        string environment,
        string status,
        bool activate,
        string message,
        CancellationToken cancellationToken = default) =>
        database.ApplyBundleDeploymentAsync(
            tenant,
            compiled,
            plan,
            environment,
            status,
            activate,
            message,
            cancellationToken);

    public Task<RuntimeRollbackResult> RollbackActiveBundleAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default) =>
        database.RollbackActiveBundleAsync(tenant, cancellationToken);

    public Task<string> CreateActivationTokenAsync(
        ResolvedTenantEnvironment tenant,
        string email,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default) =>
        database.CreateActivationTokenAsync(tenant, email, lifetime, cancellationToken);

    public Task<bool> ActivateOwnerAsync(
        ResolvedTenantEnvironment tenant,
        string token,
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken = default) =>
        database.ActivateOwnerAsync(tenant, token, email, displayName, password, cancellationToken);

    public Task<RuntimeUser?> ValidateLoginAsync(
        ResolvedTenantEnvironment tenant,
        string email,
        string password,
        CancellationToken cancellationToken = default) =>
        database.ValidateLoginAsync(tenant, email, password, cancellationToken);
}
