using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Admin;

namespace ErpGenerator.Api.Data.Configurations.Admin;

public sealed class AdminAuditRecordConfiguration : ControlPlaneEntityConfiguration<AdminAuditRecord>
{
    public override void Configure(EntityTypeBuilder<AdminAuditRecord> builder)
    {
        base.Configure(builder);
        builder.ToTable("admin_audit_records");
        builder.HasIndex(value => new { value.ActorAdminAccountId, value.CreatedAt });
    }
}
