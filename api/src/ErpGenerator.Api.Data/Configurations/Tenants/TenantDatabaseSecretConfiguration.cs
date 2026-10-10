using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Configurations.Tenants;

public sealed class TenantDatabaseSecretConfiguration : ControlPlaneEntityConfiguration<TenantDatabaseSecret>
{
    public override void Configure(EntityTypeBuilder<TenantDatabaseSecret> builder)
    {
        base.Configure(builder);
        builder.ToTable("tenant_database_secrets");
        builder.HasIndex(value => value.SecretReference).IsUnique();
        builder.HasIndex(value => new { value.TenantStableId, value.Environment, value.DatabaseName }).IsUnique();
    }
}
