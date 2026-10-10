using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Demo;

namespace ErpGenerator.Api.Data.Configurations.Demo;

public sealed class NotificationDeliveryConfiguration : ControlPlaneEntityConfiguration<NotificationDelivery>
{
    public override void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        base.Configure(builder);
        builder.ToTable("notification_deliveries");
        builder.HasIndex(value => new { value.Status, value.NextAttemptAt });
    }
}
