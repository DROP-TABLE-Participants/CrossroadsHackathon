namespace ErpGenerator.Api.Shared.Generation;

public sealed record AddEvidenceDocumentInputModel(
    string? Title,
    string? Text,
    string? SourceKind = null,
    string? SourceName = null,
    string? ContentType = null);
