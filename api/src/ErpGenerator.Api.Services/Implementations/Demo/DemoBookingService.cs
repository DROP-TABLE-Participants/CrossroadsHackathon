using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Shared.Demo;

namespace ErpGenerator.Api.Services.Implementations.Demo;

public sealed class DemoBookingService(
    ErpGeneratorDbContext dbContext,
    Microsoft.Extensions.Options.IOptions<DemoNotificationOptions> notificationOptions) : IDemoBookingService
{
    public async Task<DemoBookingAvailabilityViewModel> AvailabilityAsync(CancellationToken cancellationToken)
    {
        var ranges = await dbContext.DemoBookings.AsNoTracking()
            .Where(value => value.Status != "cancelled")
            .OrderBy(value => value.StartDate)
            .Select(value => new DemoBookingAvailabilityRange(value.StartDate, value.EndDate))
            .ToListAsync(cancellationToken);
        return new DemoBookingAvailabilityViewModel(ranges);
    }

    public async Task<DemoBookingViewModel> CreateAsync(
        Guid customerId,
        CreateDemoBookingInputModel input,
        CancellationToken cancellationToken)
    {
        var customer = await dbContext.CustomerAccounts.SingleOrDefaultAsync(value => value.Id == customerId, cancellationToken)
            ?? throw new InvalidOperationException("Customer account was not found.");
        if (await dbContext.DemoBookings.AnyAsync(value => value.CustomerId == customerId && value.Status != "cancelled", cancellationToken))
            throw new InvalidOperationException("You already have a demo booking. Please contact us to change it.");
        if (input.StartDate is null) throw new ArgumentException("Start date is required.");

        var start = new DateTimeOffset(input.StartDate.Value.UtcDateTime.Date, TimeSpan.Zero);
        var end = start.AddDays(13);
        if (await dbContext.DemoBookings.AnyAsync(
            value => value.Status != "cancelled" && value.StartDate <= end && value.EndDate >= start,
            cancellationToken))
        {
            throw new InvalidOperationException("Selected 2-week period is unavailable. Please choose another start date.");
        }

        var booking = new DemoBooking
        {
            CustomerId = customerId,
            Email = customer.Email ?? throw new InvalidOperationException("Customer email is missing."),
            PhoneNumber = Require(input.PhoneNumber ?? customer.PhoneNumber, "Phone number", 50),
            RepresentativeName = Require(input.RepresentativeName ?? customer.Name, "Representative name", 120),
            Position = Optional(input.Position ?? customer.Position, 120),
            CompanyName = Require(input.CompanyName ?? customer.CompanyName, "Company name", 160),
            StartDate = start,
            EndDate = end
        };
        dbContext.DemoBookings.Add(booking);
        booking.NotificationDeliveries.Add(new NotificationDelivery
        {
            DemoBooking = booking,
            Kind = "demo_customer_confirmation",
            Recipient = booking.Email,
            Subject = "Your ErpGenerator demo is reserved",
            Body = $"Hello {booking.RepresentativeName},\n\nYour ErpGenerator demo is reserved from {booking.StartDate:yyyy-MM-dd} through {booking.EndDate:yyyy-MM-dd}. We will contact you with the next details."
        });
        if (!string.IsNullOrWhiteSpace(notificationOptions.Value.AdminRecipient))
        {
            booking.NotificationDeliveries.Add(new NotificationDelivery
            {
                DemoBooking = booking,
                Kind = "demo_admin_alert",
                Recipient = notificationOptions.Value.AdminRecipient.Trim(),
                Subject = $"New ErpGenerator demo booking: {booking.CompanyName}",
                Body = $"{booking.RepresentativeName} ({booking.Email}) reserved {booking.StartDate:yyyy-MM-dd} through {booking.EndDate:yyyy-MM-dd}."
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(booking);
    }

    public static DemoBookingViewModel Map(DemoBooking booking) => new(
        booking.Id,
        booking.Email,
        booking.PhoneNumber,
        booking.RepresentativeName,
        booking.Position,
        booking.CompanyName,
        booking.StartDate,
        booking.EndDate,
        booking.Status);

    private static string Require(string? value, string label, int max)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length is < 1 || result.Length > max) throw new ArgumentException($"{label} is required.");
        return result;
    }

    private static string? Optional(string? value, int max)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result)) return null;
        if (result.Length > max) throw new ArgumentException("One or more fields exceed allowed length.");
        return result;
    }
}
