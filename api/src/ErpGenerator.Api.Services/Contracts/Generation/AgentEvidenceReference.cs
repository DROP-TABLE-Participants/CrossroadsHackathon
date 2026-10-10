namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentEvidenceReference(
    string SourceType,
    Guid? SourceEntityId,
    string? ExternalId,
    string Title,
    string ContentHash);
