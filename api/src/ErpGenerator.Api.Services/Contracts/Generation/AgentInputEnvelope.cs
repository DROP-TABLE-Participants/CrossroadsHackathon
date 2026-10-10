namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentInputEnvelope(
    Guid RunId,
    Guid StageId,
    string AgentKey,
    string AgentVersion,
    string ContractVersion,
    string InputManifestHash,
    string EvidenceSnapshotHash,
    IReadOnlyList<AgentEvidenceReference> Evidence,
    IReadOnlyList<AgentArtifactReference> PrerequisiteArtifacts);
