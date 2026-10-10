namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IGenerationResumeService
{
    Task<bool> TryResumeReadyAsync(
        Guid generationRunId,
        string trigger,
        CancellationToken cancellationToken);
}
