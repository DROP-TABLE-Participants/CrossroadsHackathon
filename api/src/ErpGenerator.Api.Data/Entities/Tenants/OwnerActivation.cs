using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Tenants;

public sealed class OwnerActivation : ControlPlaneEntity
{
    public Guid TenantEnvironmentId { get; set; }
    public TenantEnvironment TenantEnvironment { get; set; } = null!;
    [MaxLength(320)] public string Email { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = "pending";
    public DateTimeOffset? ActivatedAt { get; set; }
}
