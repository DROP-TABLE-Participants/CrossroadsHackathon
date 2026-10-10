namespace ErpGenerator.Api.Services.Implementations.Identity;

public static class CustomerViewModelFactory
{
    public static CustomerViewModel Create(CustomerAccount customer, string role) => new(
        customer.Id,
        customer.Email ?? string.Empty,
        customer.Name,
        customer.PhoneNumber,
        customer.CompanyName,
        customer.RepresentativeName,
        customer.Position,
        role,
        customer.IsGuest);
}
