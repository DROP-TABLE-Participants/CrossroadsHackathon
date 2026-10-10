using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Data.Entities.AdminIdentity;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Context;

public sealed class AdminIdentityDbContext(DbContextOptions<AdminIdentityDbContext> options)
    : IdentityDbContext<AdminAccount, AdminRole, Guid>(options)
{
    public DbSet<AdminAccount> AdminAccounts => Users;
    public DbSet<AdminRefreshSession> AdminRefreshSessions => Set<AdminRefreshSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("admin_identity");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AdminIdentityDbContext).Assembly,
            type => type.Namespace?.Contains(".Configurations.AdminIdentity", StringComparison.Ordinal) == true);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
