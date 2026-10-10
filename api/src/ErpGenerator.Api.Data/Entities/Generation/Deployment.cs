using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class Deployment : ControlPlaneEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public Guid ErpBundleVersionId { get; set; }
    public ErpBundleVersion ErpBundleVersion { get; set; } = null!;
    [MaxLength(24)] public string Environment { get; set; } = "staging";
    [MaxLength(32)] public string Status { get; set; } = "pending";
    [MaxLength(160)] public string OperationId { get; set; } = string.Empty;
    [MaxLength(160)] public string? RuntimeDeploymentId { get; set; }
    [MaxLength(64)] public string? DeployedBundleId { get; set; }
    [MaxLength(64)] public string? DeployedBundleVersion { get; set; }
    [MaxLength(64)] public string? DeployedBundleHash { get; set; }
    [MaxLength(64)] public string? TestSuiteHash { get; set; }
    [MaxLength(64)] public string? TestsStatus { get; set; }
    [MaxLength(64)] public string? PromotionReceiptId { get; set; }
    [MaxLength(64)] public string? PromotionReceiptPayloadHash { get; set; }
    [MaxLength(64)] public string? PromotionReceiptSignature { get; set; }
    public string? PromotionReceiptJson { get; set; }
    [MaxLength(160)] public string? LeaseOwner { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    [MaxLength(160)] public string? FailureCode { get; set; }
    [MaxLength(4000)] public string? FailureMessage { get; set; }
}
