namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record GenerationStageResult(
    GenerationStageOutcome Outcome,
    string Summary,
    object? Output = null);
