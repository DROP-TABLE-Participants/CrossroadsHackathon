namespace ErpGenerator.Api.Shared.Generation;

public sealed record GenerationEnvironmentViewModel(
    string Status,
    string? Url,
    string Environment,
    string? DeploymentId,
    string? BundleVersion,
    string? BundleHash,
    DateTimeOffset? ReadyAt,
    string? LastError,
    string? TestsStatus,
    RuntimeActivationSummaryViewModel? Activation);
