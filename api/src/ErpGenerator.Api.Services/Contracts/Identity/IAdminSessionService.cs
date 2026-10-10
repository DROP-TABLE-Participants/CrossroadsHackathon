namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IAdminSessionService
{
    Task<AdminAuthenticationViewModel> CreateAsync(Guid adminId, CancellationToken cancellationToken);
    Task<AdminAuthenticationViewModel> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken);
    Task RevokeAllAsync(Guid adminId, CancellationToken cancellationToken);
}
