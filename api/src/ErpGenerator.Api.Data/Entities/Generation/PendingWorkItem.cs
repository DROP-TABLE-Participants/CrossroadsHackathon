using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class PendingWorkItem : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    [MaxLength(64)] public string Kind { get; set; } = "more_information";
    [MaxLength(160)] public string Key { get; set; } = string.Empty;
    [MaxLength(4000)] public string Prompt { get; set; } = string.Empty;
    [MaxLength(4000)] public string? Reason { get; set; }
    [MaxLength(160)] public string? TargetRole { get; set; }
    [MaxLength(120)] public string SourceStage { get; set; } = "discovery_assessment";
    public bool Blocking { get; set; } = true;
    [MaxLength(32)] public string Status { get; set; } = "pending";
    [MaxLength(4000)] public string? ResolutionNote { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public InterviewRequest? InterviewRequest { get; set; }
}
