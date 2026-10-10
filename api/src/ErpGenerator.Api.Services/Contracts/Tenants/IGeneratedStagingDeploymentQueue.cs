namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IGeneratedStagingDeploymentQueue
{
    Task<bool> TryQueueNextAsync(CancellationToken cancellationToken);
}
