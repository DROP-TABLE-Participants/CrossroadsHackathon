using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.AdminIdentity;

namespace ErpGenerator.Api.Data.Configurations.AdminIdentity;

public sealed class AdminRefreshSessionConfiguration : ControlPlaneEntityConfiguration<AdminRefreshSession>
{
    public override void Configure(EntityTypeBuilder<AdminRefreshSession> builder)
    {
        base.Configure(builder);
        builder.ToTable("admin_refresh_sessions");
        builder.HasIndex(value => value.TokenHash).IsUnique();
        builder.HasIndex(value => new { value.AdminAccountId, value.ExpiresAt });
        builder.Property(value => value.UpdatedAt).IsConcurrencyToken();
        builder.HasOne(value => value.AdminAccount)
            .WithMany(value => value.RefreshSessions)
            .HasForeignKey(value => value.AdminAccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
