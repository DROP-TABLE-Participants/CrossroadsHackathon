namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record EvidenceEntry(
    string SourceType,
    Guid SourceEntityId,
    string? ExternalId,
    string Title,
    string Text);
