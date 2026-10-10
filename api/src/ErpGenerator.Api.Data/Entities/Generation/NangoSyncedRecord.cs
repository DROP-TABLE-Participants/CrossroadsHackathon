using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class NangoSyncedRecord : ControlPlaneEntity
{
    public Guid GenerationProjectInputSourceId { get; set; }
    public GenerationProjectInputSource GenerationProjectInputSource { get; set; } = null!;
    [MaxLength(512)] public string ExternalId { get; set; } = string.Empty;
    [MaxLength(80)] public string RecordModel { get; set; } = string.Empty;
    [MaxLength(120)] public string Provider { get; set; } = string.Empty;
    [MaxLength(240)] public string Title { get; set; } = string.Empty;
    [MaxLength(32)] public string ContentState { get; set; } = "content_extracted";
    public string Content { get; set; } = string.Empty;
    [MaxLength(64)] public string ContentSha256 { get; set; } = string.Empty;
    [MaxLength(2000)] public string? SourceUrl { get; set; }
    public string SafeMetadataJson { get; set; } = "{}";
    public DateTimeOffset? UpstreamModifiedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
