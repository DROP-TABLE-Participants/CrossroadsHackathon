using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationEvidenceSnapshotItem : ControlPlaneEntity
{
    public Guid GenerationEvidenceSnapshotId { get; set; }
    public GenerationEvidenceSnapshot GenerationEvidenceSnapshot { get; set; } = null!;
    [MaxLength(40)] public string SourceType { get; set; } = string.Empty;
    public Guid? SourceEntityId { get; set; }
    [MaxLength(512)] public string? ExternalId { get; set; }
    [MaxLength(240)] public string Title { get; set; } = string.Empty;
    [MaxLength(64)] public string ContentSha256 { get; set; } = string.Empty;
}
