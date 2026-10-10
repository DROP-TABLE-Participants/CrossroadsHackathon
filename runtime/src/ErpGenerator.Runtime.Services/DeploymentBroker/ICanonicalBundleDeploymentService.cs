using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public interface ICanonicalBundleDeploymentService
{
    Task<CanonicalBundleDeploymentResponse> DeployAsync(
        CanonicalBundleDeploymentRequest request,
        CancellationToken cancellationToken);
}
