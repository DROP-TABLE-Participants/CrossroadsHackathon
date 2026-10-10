namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IRuntimeDeploymentProcessor
{
    Task<bool> TryProcessNextAsync(string workerId, CancellationToken cancellationToken);
}
