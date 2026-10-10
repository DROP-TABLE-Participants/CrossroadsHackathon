namespace ErpGenerator.Api.Shared.Identity;

public sealed record CustomerViewModel(
    Guid Id,
    string Email,
    string Name,
    string? PhoneNumber,
    string? CompanyName,
    string? RepresentativeName,
    string? Position,
    string Role,
    bool IsGuest = false);
