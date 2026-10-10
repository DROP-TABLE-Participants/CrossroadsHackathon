namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record MappedRecord(
    string ExternalId,
    string Provider,
    string Title,
    string ContentState,
    string Content,
    string? SourceUrl,
    string SafeMetadataJson,
    DateTimeOffset? ModifiedAt);
