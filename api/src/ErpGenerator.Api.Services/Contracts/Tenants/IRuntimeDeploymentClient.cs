namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IRuntimeDeploymentClient
{
    Task<CuratedRuntimeDeploymentViewModel> DeployCuratedAsync(CuratedRuntimeDeploymentInputModel input, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<RuntimeGuestEntryViewModel> CreateGuestEntryAsync(RuntimeGuestEntryInputModel input, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<CanonicalRuntimeDeploymentViewModel> DeployCanonicalAsync(
        CanonicalRuntimeDeploymentInputModel input,
        CancellationToken cancellationToken);
    Task<RuntimeOwnerActivationViewModel> CreateActivationAsync(
        RuntimeOwnerActivationInputModel input,
        CancellationToken cancellationToken);
}
