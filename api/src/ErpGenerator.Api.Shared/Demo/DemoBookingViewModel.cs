namespace ErpGenerator.Api.Shared.Demo;

public sealed record DemoBookingViewModel(
    Guid Id,
    string Email,
    string PhoneNumber,
    string RepresentativeName,
    string? Position,
    string CompanyName,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string Status);
