namespace ErpGenerator.Api.Shared.Demo;

public sealed record DemoBookingAvailabilityRange(
    DateTimeOffset StartDate,
    DateTimeOffset EndDate);
