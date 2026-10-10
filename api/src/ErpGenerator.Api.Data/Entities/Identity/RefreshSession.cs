using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Identity;

public sealed class RefreshSession : ControlPlaneEntity
{
    public Guid CustomerAccountId { get; set; }
    public CustomerAccount CustomerAccount { get; set; } = null!;
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    [MaxLength(64)] public string? ReplacedByTokenHash { get; set; }
    [MaxLength(64)] public string? CreatedByIpHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
