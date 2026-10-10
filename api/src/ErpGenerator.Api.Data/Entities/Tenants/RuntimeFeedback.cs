using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Tenants;

public sealed class RuntimeFeedback : ControlPlaneEntity
{
    public Guid TenantEnvironmentId { get; set; }
    public TenantEnvironment TenantEnvironment { get; set; } = null!;
    [MaxLength(160)] public string RuntimeUserId { get; set; } = string.Empty;
    [MaxLength(320)] public string RuntimeUserEmail { get; set; } = string.Empty;
    [MaxLength(80)] public string? Category { get; set; }
    [MaxLength(8000)] public string Message { get; set; } = string.Empty;
    [MaxLength(160)] public string? EntityKey { get; set; }
    [MaxLength(160)] public string? RecordId { get; set; }
    [MaxLength(500)] public string? PagePath { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
