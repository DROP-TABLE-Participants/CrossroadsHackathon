namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record GenerationStageContext(
    GenerationRun Run,
    GenerationRunStage Stage,
    GenerationEvidenceSnapshot EvidenceSnapshot,
    object? PreviousOutput,
    string? PreviousFailureMessage);
