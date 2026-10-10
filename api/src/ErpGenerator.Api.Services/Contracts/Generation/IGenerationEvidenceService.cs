namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IGenerationEvidenceService
{
    Task CaptureAsync(
        Guid generationRunId,
        bool refreshConnectedSources,
        CancellationToken cancellationToken);
}
