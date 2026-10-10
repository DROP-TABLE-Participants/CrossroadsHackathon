namespace ErpGenerator.Api.Shared.Identity;

public sealed record UpdateCustomerInputModel(
    string? Name,
    string? Email,
    string? PhoneNumber,
    string? CompanyName,
    string? Position = null);
