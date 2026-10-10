using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;
using ErpGenerator.Api.Data.Entities.Identity;

namespace ErpGenerator.Api.Data.Entities.Tenants;

public sealed class Tenant : ControlPlaneEntity
{
    [MaxLength(64)] public string StableId { get; set; } = string.Empty;
    [MaxLength(80)] public string Slug { get; set; } = string.Empty;
    [MaxLength(160)] public string CompanyName { get; set; } = string.Empty;
    [MaxLength(32)] public string Plan { get; set; } = "starter";
    [MaxLength(32)] public string Status { get; set; } = "active";
    [MaxLength(32)] public string SetupStatus { get; set; } = "questions_pending";
    public Guid OwnerId { get; set; }
    public CustomerAccount Owner { get; set; } = null!;
    public ICollection<TenantEnvironment> Environments { get; set; } = [];
}
