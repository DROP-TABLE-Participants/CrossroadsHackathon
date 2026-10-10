using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationEvidenceSnapshot : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    public int Sequence { get; set; }
    public string EvidenceText { get; set; } = string.Empty;
    [MaxLength(64)] public string ContentSha256 { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public ICollection<GenerationEvidenceSnapshotItem> Items { get; set; } = [];
}
