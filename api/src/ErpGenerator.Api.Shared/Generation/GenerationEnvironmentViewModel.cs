namespace ErpGenerator.Api.Shared.Generation;

public sealed record GenerationEnvironmentViewModel(
    string Status,
    string? Url,
    string? ProductionUrl,
    string Environment,
    string? DeploymentId,
    string? BundleVersion,
    string? BundleHash,
    DateTimeOffset? ReadyAt,
    string? LastError,
    string? TestsStatus,
    bool CanPromote,
    string? PromoteDisabledReason,
    RuntimeActivationSummaryViewModel? Activation);
