using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationProjectInputSource : ControlPlaneEntity
{
    public Guid GenerationProjectId { get; set; }
    public GenerationProject GenerationProject { get; set; } = null!;
    [MaxLength(120)] public string ProviderConfigKey { get; set; } = string.Empty;
    [MaxLength(160)] public string ConnectionId { get; set; } = string.Empty;
    [MaxLength(80)] public string SourceKind { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = "pending";
    public string SafeMetadataJson { get; set; } = "{}";
    [MaxLength(32)] public string ScopeMode { get; set; } = "unconfigured";
    public string SelectedExternalIdsJson { get; set; } = "[]";
    public string ExcludedExternalIdsJson { get; set; } = "[]";
    public bool IncludeDescendants { get; set; } = true;
    public bool IncludeNewContent { get; set; } = true;
    public DateTimeOffset? AuthorizationAcceptedAt { get; set; }
    [MaxLength(32)] public string? AuthorizationNoticeVersion { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastSyncRequestedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset? RetainContentUntil { get; set; }
    public int? LastRecordCount { get; set; }
    public int? LastTextRecordCount { get; set; }
    public int? SyncDiscoveredCount { get; set; }
    public int SyncProcessedCount { get; set; }
    public int SyncFailedCount { get; set; }
    [MaxLength(500)] public string? LastError { get; set; }
    [MaxLength(500)] public string? LastWarning { get; set; }
    public ICollection<NangoSyncedRecord> SyncedRecords { get; set; } = [];
}
