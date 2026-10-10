using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;

namespace ErpGenerator.Runtime.Tests;

internal sealed class StaticControlPlaneClient(ResolvedTenantEnvironment tenant) : IControlPlaneClient
{
    public Task<ResolvedTenantEnvironment?> ResolveTenantAsync(string host, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase) ? tenant : null);

    public Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(string host, TenantEnvironmentKind environment, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase) && tenant.Environment == environment ? tenant : null);

    public Task RecordDeploymentEventAsync(DeploymentEventCreate deploymentEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateDeploymentStatusAsync(DeploymentStatusUpdate update, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateOwnerActivationStatusAsync(OwnerActivationStatusUpdate update, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubmitRuntimeFeedbackAsync(RuntimeFeedbackCreate feedback, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubmitRuntimeUsageEventAsync(RuntimeUsageEventCreate usageEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class MultiTenantControlPlaneClient(params ResolvedTenantEnvironment[] tenants) : IControlPlaneClient
{
    public Task<ResolvedTenantEnvironment?> ResolveTenantAsync(string host, CancellationToken cancellationToken = default) =>
        Task.FromResult(tenants.FirstOrDefault(tenant => string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase)));

    public Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(string host, TenantEnvironmentKind environment, CancellationToken cancellationToken = default) =>
        Task.FromResult(tenants.FirstOrDefault(tenant => string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase) && tenant.Environment == environment));

    public Task RecordDeploymentEventAsync(DeploymentEventCreate deploymentEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateDeploymentStatusAsync(DeploymentStatusUpdate update, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateOwnerActivationStatusAsync(OwnerActivationStatusUpdate update, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubmitRuntimeFeedbackAsync(RuntimeFeedbackCreate feedback, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubmitRuntimeUsageEventAsync(RuntimeUsageEventCreate usageEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class CapturingUsageControlPlaneClient(params ResolvedTenantEnvironment[] tenants) : IControlPlaneClient
{
    public List<RuntimeFeedbackCreate> Feedback { get; } = [];

    public List<RuntimeUsageEventCreate> UsageEvents { get; } = [];

    public Exception? UsageEventFailure { get; set; }

    public Task<ResolvedTenantEnvironment?> ResolveTenantAsync(string host, CancellationToken cancellationToken = default) =>
        Task.FromResult(tenants.FirstOrDefault(tenant => string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase)));

    public Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(string host, TenantEnvironmentKind environment, CancellationToken cancellationToken = default) =>
        Task.FromResult(tenants.FirstOrDefault(tenant => string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase) && tenant.Environment == environment));

    public Task RecordDeploymentEventAsync(DeploymentEventCreate deploymentEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateDeploymentStatusAsync(DeploymentStatusUpdate update, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateOwnerActivationStatusAsync(OwnerActivationStatusUpdate update, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubmitRuntimeFeedbackAsync(RuntimeFeedbackCreate feedback, CancellationToken cancellationToken = default)
    {
        Feedback.Add(feedback);
        return Task.CompletedTask;
    }

    public Task SubmitRuntimeUsageEventAsync(RuntimeUsageEventCreate usageEvent, CancellationToken cancellationToken = default)
    {
        if (UsageEventFailure is not null)
        {
            throw UsageEventFailure;
        }

        UsageEvents.Add(usageEvent);
        return Task.CompletedTask;
    }
}
