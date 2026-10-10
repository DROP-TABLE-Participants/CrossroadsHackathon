using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;
using ErpGenerator.Api.Data.Entities.Identity;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Entities.Demo;

public sealed class DemoInstance : ControlPlaneEntity
{
    public Guid? CustomerId { get; set; }
    public CustomerAccount? Customer { get; set; }
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public Guid? ReplacesId { get; set; }
    [MaxLength(80)] public string TemplateVersion { get; set; } = "dandelion-chocolate.v1";
    [MaxLength(32)] public string Status { get; set; } = "queued";
    public string? DeploymentReceiptJson { get; set; }
    [MaxLength(500)] public string? FailureMessage { get; set; }
}
