namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IRuntimeLifecycleService
{
    Task<GenerationRunActionViewModel> RequestProductionPromotionAsync(
        Guid customerId, Guid runId, bool confirm, CancellationToken cancellationToken);
    Task<OneTimeActivationLinkViewModel> CreateActivationLinkAsync(
        Guid customerId, Guid runId, string environment, CancellationToken cancellationToken);
}
