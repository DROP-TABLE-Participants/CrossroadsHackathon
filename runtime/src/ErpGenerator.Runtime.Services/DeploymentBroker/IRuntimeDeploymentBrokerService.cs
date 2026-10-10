using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public interface IRuntimeDeploymentBrokerService
{
    Task<RuntimeErpDeploymentBrokerResponse> DeployCuratedAsync(RuntimeCuratedDeploymentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<RuntimeGuestEntryResponse> CreateGuestEntryAsync(RuntimeGuestEntryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<RuntimeErpDeploymentBrokerResponse> DeployAsync(
        RuntimeErpDeploymentBrokerRequest request,
        CancellationToken cancellationToken = default);

    Task<RuntimeErpResetBrokerResponse> ResetAsync(
        RuntimeErpResetBrokerRequest request,
        CancellationToken cancellationToken = default);

    Task<RuntimeOwnerActivationBrokerResponse> CreateOwnerActivationAsync(
        RuntimeOwnerActivationBrokerRequest request,
        CancellationToken cancellationToken = default);
}
