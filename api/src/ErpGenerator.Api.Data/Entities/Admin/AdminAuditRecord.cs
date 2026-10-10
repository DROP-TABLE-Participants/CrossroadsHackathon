using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Admin;

public sealed class AdminAuditRecord : ControlPlaneEntity
{
    public Guid ActorAdminAccountId { get; set; }
    [MaxLength(320)] public string ActorEmailSnapshot { get; set; } = string.Empty;
    [MaxLength(100)] public string Action { get; set; } = string.Empty;
    [MaxLength(80)] public string ResourceType { get; set; } = string.Empty;
    [MaxLength(160)] public string ResourceId { get; set; } = string.Empty;
    public string SafeDetailsJson { get; set; } = "{}";
}
