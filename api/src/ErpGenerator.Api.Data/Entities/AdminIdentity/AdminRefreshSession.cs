using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.AdminIdentity;

public sealed class AdminRefreshSession : ControlPlaneEntity
{
    public Guid AdminAccountId { get; set; }
    public AdminAccount AdminAccount { get; set; } = null!;
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    [MaxLength(64)] public string? ReplacedByTokenHash { get; set; }
    [MaxLength(64)] public string? CreatedByIpHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
