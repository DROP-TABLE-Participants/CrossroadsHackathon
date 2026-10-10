namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface ICustomerService
{
    Task<CustomerAuthenticationViewModel> CreateGuestAsync(CancellationToken cancellationToken);
    Task<CustomerAuthenticationViewModel> RegisterAsync(RegisterCustomerInputModel input, CancellationToken cancellationToken);
    Task<CustomerAuthenticationViewModel> LoginAsync(LoginCustomerInputModel input, CancellationToken cancellationToken);
    Task<CustomerAuthenticationViewModel> RefreshAsync(RefreshCustomerSessionInputModel input, CancellationToken cancellationToken);
    Task LogoutAsync(LogoutCustomerSessionInputModel input, CancellationToken cancellationToken);
    Task<CurrentCustomerViewModel?> GetCurrentAsync(Guid customerId, CancellationToken cancellationToken);
    Task<CurrentCustomerViewModel> UpdateAsync(Guid customerId, UpdateCustomerInputModel input, CancellationToken cancellationToken);
}
