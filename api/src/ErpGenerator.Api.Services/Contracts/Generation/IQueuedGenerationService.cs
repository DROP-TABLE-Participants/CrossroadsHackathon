namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IQueuedGenerationService
{
    Task<bool> TryResumeNextReadyAsync(CancellationToken cancellationToken);
    Task<bool> TryStartNextReadyAsync(CancellationToken cancellationToken);
}
