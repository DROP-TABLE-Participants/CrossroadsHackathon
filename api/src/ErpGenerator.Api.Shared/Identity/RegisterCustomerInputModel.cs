namespace ErpGenerator.Api.Shared.Identity;

public sealed record RegisterCustomerInputModel(
    string? Name,
    string? Email,
    string? Password,
    string? PhoneNumber = null,
    string? CompanyName = null,
    string? Position = null);
