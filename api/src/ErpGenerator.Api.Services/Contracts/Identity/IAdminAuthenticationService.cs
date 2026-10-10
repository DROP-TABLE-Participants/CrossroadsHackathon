namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IAdminAuthenticationService
{
    Task<AdminAuthenticationViewModel> LoginAsync(LoginAdminInputModel input, CancellationToken cancellationToken);
    Task<AdminAuthenticationViewModel> RefreshAsync(
        RefreshAdminSessionInputModel input,
        CancellationToken cancellationToken);
    Task LogoutAsync(LogoutAdminSessionInputModel input, CancellationToken cancellationToken);
    Task<AdminViewModel?> GetCurrentAsync(Guid adminId, CancellationToken cancellationToken);
}
