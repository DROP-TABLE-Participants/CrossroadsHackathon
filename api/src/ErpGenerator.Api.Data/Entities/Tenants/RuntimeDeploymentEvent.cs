using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Tenants;

public sealed class RuntimeDeploymentEvent : ControlPlaneEntity
{
    public Guid TenantEnvironmentId { get; set; }
    public TenantEnvironment TenantEnvironment { get; set; } = null!;
    [MaxLength(80)] public string Type { get; set; } = string.Empty;
    [MaxLength(4000)] public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
}
