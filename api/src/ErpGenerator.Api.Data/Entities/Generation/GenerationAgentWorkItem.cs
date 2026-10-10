using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationAgentWorkItem : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    public Guid GenerationRunStageId { get; set; }
    public GenerationRunStage GenerationRunStage { get; set; } = null!;
    public Guid? ParentWorkItemId { get; set; }
    public GenerationAgentWorkItem? ParentWorkItem { get; set; }
    [MaxLength(120)] public string WorkItemKey { get; set; } = string.Empty;
    [MaxLength(120)] public string AgentKey { get; set; } = string.Empty;
    [MaxLength(40)] public string AgentVersion { get; set; } = string.Empty;
    [MaxLength(80)] public string AgentRole { get; set; } = string.Empty;
    [MaxLength(80)] public string AgentLayer { get; set; } = string.Empty;
    [MaxLength(40)] public string ContractVersion { get; set; } = string.Empty;
    [MaxLength(64)] public string DefinitionHash { get; set; } = string.Empty;
    [MaxLength(120)] public string? SupervisorAgentKey { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "queued";
    public int Priority { get; set; }
    public int Sequence { get; set; }
    [MaxLength(64)] public string InputManifestHash { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public int RepairCount { get; set; }
    public int RoutingDepth { get; set; }
    [MaxLength(160)] public string? LeaseOwner { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    [MaxLength(32)] public string? FailureDisposition { get; set; }
    [MaxLength(160)] public string? FailureCode { get; set; }
    [MaxLength(4000)] public string? FailureMessage { get; set; }
    [MaxLength(4000)] public string? Summary { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<GenerationAgentWorkItem> ChildWorkItems { get; set; } = [];
    public ICollection<GenerationAgentWorkItemDependency> Dependencies { get; set; } = [];
    public ICollection<GenerationAgentWorkItemDependency> Dependents { get; set; } = [];
    public ICollection<GenerationAgentExecutionAttempt> Attempts { get; set; } = [];
    public ICollection<GenerationArtifactVersion> Artifacts { get; set; } = [];
}
