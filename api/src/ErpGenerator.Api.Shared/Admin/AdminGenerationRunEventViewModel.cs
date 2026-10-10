namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationRunEventViewModel(
    Guid Id,
    int Sequence,
    string EventType,
    string? StageKey,
    int? AttemptNumber,
    string Summary,
    string SafeMetadataJson,
    DateTimeOffset CreatedAt);
