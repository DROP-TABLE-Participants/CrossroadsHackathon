namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IGeneratedErpDeploymentQueue
{
    Task<bool> TryQueueNextAsync(CancellationToken cancellationToken);
}
