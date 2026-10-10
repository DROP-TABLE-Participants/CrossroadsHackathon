using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class NangoSyncedRecordConfiguration : ControlPlaneEntityConfiguration<NangoSyncedRecord>
{
    public override void Configure(EntityTypeBuilder<NangoSyncedRecord> builder)
    {
        base.Configure(builder);
        builder.ToTable("nango_synced_records");
        builder.HasIndex(value => new { value.GenerationProjectInputSourceId, value.RecordModel, value.ExternalId }).IsUnique();
    }
}
