using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.AdminIdentity;

public sealed class AdminAccount : IdentityUser<Guid>, IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public ICollection<AdminRefreshSession> RefreshSessions { get; set; } = [];
}
