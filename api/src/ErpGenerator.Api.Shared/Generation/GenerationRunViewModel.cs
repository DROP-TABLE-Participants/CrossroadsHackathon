namespace ErpGenerator.Api.Shared.Generation;

public sealed record GenerationRunViewModel(
    Guid Id,
    string Status,
    string RequestedMode,
    string? ActualMode,
    string? Summary,
    string? LastError,
    DateTimeOffset? LastEventAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    GenerationEnvironmentViewModel? Erp,
    GeneratedErpSummaryViewModel? GeneratedErp,
    IReadOnlyList<GenerationStageViewModel> Stages,
    IReadOnlyList<GenerationArtifactViewModel> Artifacts,
    IReadOnlyList<PendingWorkItemViewModel> PendingWorkItems);
