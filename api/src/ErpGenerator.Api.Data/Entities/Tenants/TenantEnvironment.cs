using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Tenants;

public sealed class TenantEnvironment : ControlPlaneEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    [MaxLength(24)] public string Environment { get; set; } = "erp";
    [MaxLength(253)] public string Host { get; set; } = string.Empty;
    [MaxLength(63)] public string DatabaseName { get; set; } = string.Empty;
    [MaxLength(200)] public string? CredentialSecretReference { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "pending";
    [MaxLength(500)] public string? RuntimeUrl { get; set; }
    [MaxLength(120)] public string? ActiveBundleId { get; set; }
    [MaxLength(64)] public string? ActiveBundleVersion { get; set; }
    [MaxLength(64)] public string? ActiveBundleHash { get; set; }
    [MaxLength(500)] public string? OwnerActivationUrl { get; set; }
    public DateTimeOffset? OwnerActivationExpiresAt { get; set; }
    public ICollection<OwnerActivation> OwnerActivations { get; set; } = [];
}
