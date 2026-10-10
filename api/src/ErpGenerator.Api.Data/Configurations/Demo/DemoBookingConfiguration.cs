using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Demo;

namespace ErpGenerator.Api.Data.Configurations.Demo;

public sealed class DemoBookingConfiguration : ControlPlaneEntityConfiguration<DemoBooking>
{
    public override void Configure(EntityTypeBuilder<DemoBooking> builder)
    {
        base.Configure(builder);
        builder.ToTable("demo_bookings");
        builder.HasIndex(value => new { value.CustomerId, value.Status });
        builder.HasIndex(value => new { value.StartDate, value.EndDate });
    }
}
