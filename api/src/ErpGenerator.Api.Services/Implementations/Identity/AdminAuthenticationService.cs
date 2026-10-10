using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class AdminAuthenticationService(
    AdminIdentityDbContext dbContext,
    UserManager<AdminAccount> userManager,
    IAdminSessionService sessions) : IAdminAuthenticationService
{
    public async Task<AdminAuthenticationViewModel> LoginAsync(
        LoginAdminInputModel input,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(input.Email);
        var admin = await userManager.FindByEmailAsync(email);
        if (admin is null ||
            !admin.IsActive ||
            await userManager.IsLockedOutAsync(admin) ||
            !await userManager.IsInRoleAsync(admin, IdentityRoleNames.PlatformAdministrator))
            throw new AdminAuthenticationException("Invalid login credentials.");

        if (!await userManager.CheckPasswordAsync(admin, input.Password ?? string.Empty))
        {
            await userManager.AccessFailedAsync(admin);
            throw new AdminAuthenticationException("Invalid login credentials.");
        }

        await userManager.ResetAccessFailedCountAsync(admin);
        return await sessions.CreateAsync(admin.Id, cancellationToken);
    }

    public Task<AdminAuthenticationViewModel> RefreshAsync(
        RefreshAdminSessionInputModel input,
        CancellationToken cancellationToken) =>
        sessions.RefreshAsync(input.RefreshToken?.Trim() ?? string.Empty, cancellationToken);

    public Task LogoutAsync(LogoutAdminSessionInputModel input, CancellationToken cancellationToken) =>
        sessions.RevokeAsync(input.RefreshToken?.Trim(), cancellationToken);

    public async Task<AdminViewModel?> GetCurrentAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var admin = await dbContext.AdminAccounts.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == adminId && value.IsActive,
            cancellationToken);
        if (admin is null) return null;
        var roles = await userManager.GetRolesAsync(admin);
        if (!roles.Contains(IdentityRoleNames.PlatformAdministrator, StringComparer.Ordinal))
            return null;
        return new AdminViewModel(
            admin.Id,
            admin.Email ?? string.Empty,
            admin.Name,
            IdentityRoleNames.PlatformAdministrator);
    }

    private static string NormalizeEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (email.Length is < 3 or > 320 || !email.Contains('@', StringComparison.Ordinal))
            throw new AdminAuthenticationException("Invalid login credentials.");
        return email;
    }
}
