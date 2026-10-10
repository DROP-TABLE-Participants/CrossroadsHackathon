using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Configurations.Common;

public abstract class ControlPlaneEntityConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : ControlPlaneEntity
{
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.Property(value => value.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(value => value.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}
