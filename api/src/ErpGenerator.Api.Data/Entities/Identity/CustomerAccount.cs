using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using ErpGenerator.Api.Data.Entities.Common;
using ErpGenerator.Api.Data.Entities.Generation;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Entities.Identity;

public sealed class CustomerAccount : IdentityUser<Guid>, IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(160)] public string? CompanyName { get; set; }
    [MaxLength(120)] public string? RepresentativeName { get; set; }
    [MaxLength(120)] public string? Position { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsGuest { get; set; }
    public ICollection<RefreshSession> RefreshSessions { get; set; } = [];
    public ICollection<Tenant> Tenants { get; set; } = [];
    public ICollection<GenerationProject> GenerationProjects { get; set; } = [];
}
