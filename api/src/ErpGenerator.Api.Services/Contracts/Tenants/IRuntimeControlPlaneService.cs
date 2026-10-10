namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IRuntimeControlPlaneService
{
    Task<RuntimeTenantEnvironmentViewModel?> ResolveAsync(string host, string? environment, CancellationToken cancellationToken);
    Task<RuntimeScheduleTargetsViewModel> ListScheduleTargetsAsync(int page, int limit, CancellationToken cancellationToken);
    Task StoreCredentialAsync(StoreTenantDatabaseCredentialInputModel input, CancellationToken cancellationToken);
    Task<TenantDatabaseCredentialViewModel?> ResolveCredentialAsync(string secretReference, CancellationToken cancellationToken);
    Task RecordDeploymentEventAsync(RuntimeDeploymentEventInputModel input, CancellationToken cancellationToken);
    Task UpdateDeploymentStatusAsync(RuntimeDeploymentStatusInputModel input, CancellationToken cancellationToken);
    Task UpdateOwnerActivationStatusAsync(RuntimeOwnerActivationStatusInputModel input, CancellationToken cancellationToken);
    Task SubmitFeedbackAsync(RuntimeFeedbackInputModel input, CancellationToken cancellationToken);
    Task SubmitUsageAsync(RuntimeUsageEventInputModel input, CancellationToken cancellationToken);
}
