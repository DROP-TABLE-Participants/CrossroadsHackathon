namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationArtifactVersionViewModel(
    Guid Id,
    Guid WorkItemId,
    Guid AttemptId,
    Guid? SupersedesArtifactVersionId,
    string ArtifactKey,
    string ArtifactType,
    string SchemaName,
    string SchemaVersion,
    int Version,
    string ContentHash,
    string InputManifestHash,
    string EvidenceSnapshotHash,
    string ProducerAgentKey,
    string ProducerAgentVersion,
    string ProducerDefinitionHash,
    string ValidationStatus,
    string? ValidationSummary,
    DateTimeOffset CreatedAt);
