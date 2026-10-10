namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IRuntimeLifecycleService
{
    Task<OneTimeActivationLinkViewModel> CreateActivationLinkAsync(
        Guid customerId, Guid runId, CancellationToken cancellationToken);
}
