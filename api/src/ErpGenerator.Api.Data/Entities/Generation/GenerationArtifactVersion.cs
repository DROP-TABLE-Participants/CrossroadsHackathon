using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationArtifactVersion : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    public Guid GenerationRunStageId { get; set; }
    public GenerationRunStage GenerationRunStage { get; set; } = null!;
    public Guid GenerationAgentWorkItemId { get; set; }
    public GenerationAgentWorkItem GenerationAgentWorkItem { get; set; } = null!;
    public Guid GenerationAgentExecutionAttemptId { get; set; }
    public GenerationAgentExecutionAttempt GenerationAgentExecutionAttempt { get; set; } = null!;
    public Guid? SupersedesArtifactVersionId { get; set; }
    public GenerationArtifactVersion? SupersedesArtifactVersion { get; set; }
    [MaxLength(120)] public string ArtifactKey { get; set; } = string.Empty;
    [MaxLength(120)] public string ArtifactType { get; set; } = string.Empty;
    [MaxLength(80)] public string SchemaName { get; set; } = string.Empty;
    [MaxLength(40)] public string SchemaVersion { get; set; } = string.Empty;
    public int Version { get; set; }
    [MaxLength(64)] public string ContentHash { get; set; } = string.Empty;
    [MaxLength(64)] public string InputManifestHash { get; set; } = string.Empty;
    [MaxLength(64)] public string EvidenceSnapshotHash { get; set; } = string.Empty;
    [MaxLength(120)] public string ProducerAgentKey { get; set; } = string.Empty;
    [MaxLength(40)] public string ProducerAgentVersion { get; set; } = string.Empty;
    [MaxLength(64)] public string ProducerDefinitionHash { get; set; } = string.Empty;
    public string CanonicalJson { get; set; } = string.Empty;
    [MaxLength(32)] public string ValidationStatus { get; set; } = "valid";
    [MaxLength(4000)] public string? ValidationSummary { get; set; }
    public ICollection<GenerationArtifactVersion> SupersedingVersions { get; set; } = [];
    public ICollection<ErpBundleVersion> ErpBundleVersions { get; set; } = [];
}
