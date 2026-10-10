namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface ICustomerSessionService
{
    Task<CustomerAuthenticationViewModel> CreateAsync(Guid customerId, CancellationToken cancellationToken);
    Task<CustomerAuthenticationViewModel> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken);
    Task RevokeAllAsync(Guid customerId, CancellationToken cancellationToken);
}
