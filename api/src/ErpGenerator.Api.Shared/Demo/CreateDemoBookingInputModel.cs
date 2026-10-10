namespace ErpGenerator.Api.Shared.Demo;

public sealed record CreateDemoBookingInputModel(
    string? Email,
    string? PhoneNumber,
    string? RepresentativeName,
    string? Position,
    string? CompanyName,
    DateTimeOffset? StartDate);
