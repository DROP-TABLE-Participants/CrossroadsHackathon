namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentArtifactReference(
    Guid ArtifactVersionId,
    string ArtifactType,
    int Version,
    string ContentHash);
