namespace ErpGenerator.Api.Services.Implementations.Generation;

internal interface IGenerationStageHandler
{
    string StageKey { get; }
    int Sequence { get; }
    Task<GenerationStageResult?> RestoreCompletedResultAsync(
        GenerationStageContext context,
        CancellationToken cancellationToken);
    Task<GenerationStageResult> ExecuteAsync(
        GenerationStageContext context,
        CancellationToken cancellationToken);
}
