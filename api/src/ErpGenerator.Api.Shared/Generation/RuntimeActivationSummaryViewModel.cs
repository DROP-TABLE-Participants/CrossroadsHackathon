namespace ErpGenerator.Api.Shared.Generation;

public sealed record RuntimeActivationSummaryViewModel(
    string Status,
    bool ActivationUrlAvailable,
    DateTimeOffset? ActivationCreatedAt,
    DateTimeOffset? ActivationExpiresAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? ActivationLastGeneratedAt,
    string ActivationEnvironment,
    string? ActivationEmail);
