using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.ControlPlane;

public interface IControlPlaneClient
{
    Task<ResolvedTenantEnvironment?> ResolveTenantAsync(string host, CancellationToken cancellationToken = default);

    Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(
        string host,
        TenantEnvironmentKind environment,
        CancellationToken cancellationToken = default);

    Task<ControlPlaneScheduleTargetsPage> ListScheduleTargetsAsync(
        int page,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ControlPlaneScheduleTargetsPage(page, false, []));

    Task RecordDeploymentEventAsync(DeploymentEventCreate deploymentEvent, CancellationToken cancellationToken = default);

    Task UpdateDeploymentStatusAsync(DeploymentStatusUpdate update, CancellationToken cancellationToken = default);

    Task UpdateOwnerActivationStatusAsync(OwnerActivationStatusUpdate update, CancellationToken cancellationToken = default);

    Task SubmitRuntimeFeedbackAsync(RuntimeFeedbackCreate feedback, CancellationToken cancellationToken = default);

    Task SubmitRuntimeUsageEventAsync(RuntimeUsageEventCreate usageEvent, CancellationToken cancellationToken = default);
}
