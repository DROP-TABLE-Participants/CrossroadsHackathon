using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationRun : ControlPlaneEntity
{
    public Guid GenerationProjectId { get; set; }
    public GenerationProject GenerationProject { get; set; } = null!;
    [MaxLength(32)] public string Status { get; set; } = "queued";
    [MaxLength(32)] public string RequestedMode { get; set; } = "default";
    [MaxLength(32)] public string? ActualMode { get; set; }
    [MaxLength(160)] public string? LeaseOwner { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int LastEventSequence { get; set; }
    public int ResumeAttemptCount { get; set; }
    public int ResumeFailureCount { get; set; }
    public DateTimeOffset? ResumeLastAttemptAt { get; set; }
    [MaxLength(1000)] public string? ResumeFailureMessage { get; set; }
    public int ClaimCount { get; set; }
    public int FailureCount { get; set; }
    [MaxLength(32)] public string? FailureDisposition { get; set; }
    [MaxLength(160)] public string? FailureCode { get; set; }
    [MaxLength(4000)] public string? FailureMessage { get; set; }
    public string WarningsJson { get; set; } = "[]";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public ICollection<GenerationRunStage> Stages { get; set; } = [];
    public ICollection<GenerationRunEvent> Events { get; set; } = [];
    public ICollection<PendingWorkItem> PendingWorkItems { get; set; } = [];
    public ICollection<ModelCallRecord> ModelCalls { get; set; } = [];
    public ICollection<ErpBundleVersion> BundleVersions { get; set; } = [];
    public ICollection<GenerationEvidenceSnapshot> EvidenceSnapshots { get; set; } = [];
    public ICollection<GenerationAgentWorkItem> AgentWorkItems { get; set; } = [];
    public ICollection<GenerationArtifactVersion> ArtifactVersions { get; set; } = [];
}
