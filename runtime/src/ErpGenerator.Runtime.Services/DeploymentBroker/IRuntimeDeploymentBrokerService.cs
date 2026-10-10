using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public interface IRuntimeDeploymentBrokerService
{
    Task<RuntimeStagingDeploymentBrokerResponse> DeployCuratedAsync(RuntimeCuratedDeploymentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<RuntimeGuestEntryResponse> CreateGuestEntryAsync(RuntimeGuestEntryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<RuntimeStagingDeploymentBrokerResponse> DeployStagingAsync(
        RuntimeStagingDeploymentBrokerRequest request,
        CancellationToken cancellationToken = default);

    Task<RuntimeStagingResetBrokerResponse> ResetStagingAsync(
        RuntimeStagingResetBrokerRequest request,
        CancellationToken cancellationToken = default);

    Task<RuntimeProductionPromotionBrokerResponse> PromoteProductionAsync(
        RuntimeProductionPromotionBrokerRequest request,
        CancellationToken cancellationToken = default);

    Task<RuntimeOwnerActivationBrokerResponse> CreateOwnerActivationAsync(
        RuntimeOwnerActivationBrokerRequest request,
        CancellationToken cancellationToken = default);
}
