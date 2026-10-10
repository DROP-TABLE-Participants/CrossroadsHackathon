namespace ErpGenerator.Api.Shared.Identity;

public sealed record CurrentCustomerViewModel(
    CustomerViewModel User,
    Demo.DemoBookingViewModel? Booking);
