namespace ErpGenerator.Api.Shared.Generation;

public sealed record OneTimeActivationLinkViewModel(
    string Environment,
    string Status,
    string ActivationUrl,
    string? ActivationEmail,
    DateTimeOffset? ActivationExpiresAt);
