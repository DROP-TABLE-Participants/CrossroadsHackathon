using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationRunEvent : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    public int Sequence { get; set; }
    [MaxLength(80)] public string EventType { get; set; } = string.Empty;
    [MaxLength(120)] public string? StageKey { get; set; }
    public int? AttemptNumber { get; set; }
    [MaxLength(1000)] public string Summary { get; set; } = string.Empty;
    [MaxLength(4000)] public string SafeMetadataJson { get; set; } = "{}";
}
