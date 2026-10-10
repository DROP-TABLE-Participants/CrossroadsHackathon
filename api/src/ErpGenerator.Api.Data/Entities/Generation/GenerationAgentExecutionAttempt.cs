using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationAgentExecutionAttempt : ControlPlaneEntity
{
    public Guid GenerationAgentWorkItemId { get; set; }
    public GenerationAgentWorkItem GenerationAgentWorkItem { get; set; } = null!;
    public int AttemptNumber { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "running";
    [MaxLength(160)] public string WorkerId { get; set; } = string.Empty;
    public Guid LeaseId { get; set; }
    [MaxLength(120)] public string AgentKey { get; set; } = string.Empty;
    [MaxLength(40)] public string AgentVersion { get; set; } = string.Empty;
    [MaxLength(64)] public string DefinitionHash { get; set; } = string.Empty;
    [MaxLength(64)] public string InputManifestHash { get; set; } = string.Empty;
    public string InputEnvelopeJson { get; set; } = string.Empty;
    [MaxLength(64)] public string? OutputArtifactHash { get; set; }
    [MaxLength(32)] public string? FailureDisposition { get; set; }
    [MaxLength(160)] public string? FailureCode { get; set; }
    [MaxLength(4000)] public string? FailureMessage { get; set; }
    [MaxLength(4000)] public string? RepairInstructions { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<ModelCallRecord> ModelCalls { get; set; } = [];
    public ICollection<GenerationArtifactVersion> Artifacts { get; set; } = [];
}
