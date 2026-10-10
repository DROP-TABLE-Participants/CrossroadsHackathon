namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationRunActionViewModel(
    Guid RunId,
    string Status,
    DateTimeOffset? NextAttemptAt);
