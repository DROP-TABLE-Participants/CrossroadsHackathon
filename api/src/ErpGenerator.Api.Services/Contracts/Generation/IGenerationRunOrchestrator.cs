namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IGenerationRunOrchestrator
{
    Task<bool> TryProcessNextAsync(string workerId, CancellationToken cancellationToken);
}
