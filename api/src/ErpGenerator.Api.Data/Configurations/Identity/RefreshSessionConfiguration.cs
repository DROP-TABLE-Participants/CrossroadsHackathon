using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Identity;

namespace ErpGenerator.Api.Data.Configurations.Identity;

public sealed class RefreshSessionConfiguration : ControlPlaneEntityConfiguration<RefreshSession>
{
    public override void Configure(EntityTypeBuilder<RefreshSession> builder)
    {
        base.Configure(builder);
        builder.ToTable("refresh_sessions");
        builder.HasIndex(value => value.TokenHash).IsUnique();
        builder.HasIndex(value => new { value.CustomerAccountId, value.ExpiresAt });
        builder.Property(value => value.UpdatedAt).IsConcurrencyToken();
        builder.HasOne(value => value.CustomerAccount)
            .WithMany(value => value.RefreshSessions)
            .HasForeignKey(value => value.CustomerAccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
