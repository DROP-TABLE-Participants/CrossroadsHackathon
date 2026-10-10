using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationRunStage : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    [MaxLength(120)] public string StageKey { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = "running";
    public int Sequence { get; set; }
    public int AttemptCount { get; set; }
    [MaxLength(64)] public string? InputHash { get; set; }
    [MaxLength(160)] public string? FailureCode { get; set; }
    [MaxLength(4000)] public string? FailureMessage { get; set; }
    [MaxLength(4000)] public string? Summary { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<GenerationAgentWorkItem> AgentWorkItems { get; set; } = [];
    public ICollection<GenerationArtifactVersion> ArtifactVersions { get; set; } = [];
}
