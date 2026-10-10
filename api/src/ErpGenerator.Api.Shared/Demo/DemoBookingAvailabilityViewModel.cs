namespace ErpGenerator.Api.Shared.Demo;

public sealed record DemoBookingAvailabilityViewModel(
    IReadOnlyList<DemoBookingAvailabilityRange> Ranges);
