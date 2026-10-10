using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class AdminSessionService(
    AdminIdentityDbContext dbContext,
    UserManager<AdminAccount> userManager,
    IAdminAccessTokenIssuer accessTokenIssuer,
    IOptions<AdminSessionOptions> options) : IAdminSessionService
{
    public async Task<AdminAuthenticationViewModel> CreateAsync(
        Guid adminId,
        CancellationToken cancellationToken)
    {
        var admin = await dbContext.AdminAccounts.SingleOrDefaultAsync(
            value => value.Id == adminId,
            cancellationToken);
        if (admin is null || !admin.IsActive)
            throw new AdminAuthenticationException("Administrator account was not found.");

        var roles = await userManager.GetRolesAsync(admin);
        EnsurePlatformAdministrator(roles);
        var accessToken = accessTokenIssuer.Issue(
            new AccessTokenSubject(admin.Id, admin.Email, admin.SecurityStamp ?? string.Empty),
            roles.ToArray());
        var refreshToken = CreateToken();
        var refreshExpiresAt = DateTimeOffset.UtcNow.AddDays(Math.Clamp(options.Value.RefreshLifetimeDays, 1, 30));
        dbContext.AdminRefreshSessions.Add(new AdminRefreshSession
        {
            AdminAccountId = admin.Id,
            TokenHash = Hash(refreshToken),
            ExpiresAt = refreshExpiresAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Authentication(admin, roles, accessToken, refreshToken, refreshExpiresAt);
    }

    public async Task<AdminAuthenticationViewModel> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new AdminAuthenticationException("Invalid session.");
        var tokenHash = Hash(refreshToken);
        var session = await dbContext.AdminRefreshSessions
            .Include(value => value.AdminAccount)
            .SingleOrDefaultAsync(value => value.TokenHash == tokenHash, cancellationToken)
            ?? throw new AdminAuthenticationException("Invalid session.");

        var now = DateTimeOffset.UtcNow;
        if (session.RevokedAt is not null)
        {
            if (session.ReplacedByTokenHash is not null)
            {
                await RevokeAllAsync(session.AdminAccountId, cancellationToken);
            }
            throw new AdminAuthenticationException("Invalid session.");
        }
        if (session.ExpiresAt <= now || !session.AdminAccount.IsActive)
        {
            session.RevokedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new AdminAuthenticationException("Invalid session.");
        }

        var roles = await userManager.GetRolesAsync(session.AdminAccount);
        if (!roles.Contains(IdentityRoleNames.PlatformAdministrator, StringComparer.Ordinal))
        {
            session.RevokedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new AdminAuthenticationException("Invalid session.");
        }

        var replacementToken = CreateToken();
        var replacementHash = Hash(replacementToken);
        var replacementExpiresAt = now.AddDays(Math.Clamp(options.Value.RefreshLifetimeDays, 1, 30));
        session.RevokedAt = now;
        session.ReplacedByTokenHash = replacementHash;
        dbContext.AdminRefreshSessions.Add(new AdminRefreshSession
        {
            AdminAccountId = session.AdminAccountId,
            TokenHash = replacementHash,
            ExpiresAt = replacementExpiresAt
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AdminAuthenticationException("Invalid session.");
        }

        var accessToken = accessTokenIssuer.Issue(
            new AccessTokenSubject(
                session.AdminAccount.Id,
                session.AdminAccount.Email,
                session.AdminAccount.SecurityStamp ?? string.Empty),
            roles.ToArray());
        return Authentication(session.AdminAccount, roles, accessToken, replacementToken, replacementExpiresAt);
    }

    public async Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var tokenHash = Hash(refreshToken);
        var session = await dbContext.AdminRefreshSessions.SingleOrDefaultAsync(
            value => value.TokenHash == tokenHash,
            cancellationToken);
        if (session is null || session.RevokedAt is not null) return;
        session.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var sessions = await dbContext.AdminRefreshSessions
            .Where(value => value.AdminAccountId == adminId && value.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var session in sessions) session.RevokedAt = now;
        if (sessions.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AdminAuthenticationViewModel Authentication(
        AdminAccount admin,
        IList<string> roles,
        AccessTokenViewModel accessToken,
        string refreshToken,
        DateTimeOffset refreshExpiresAt) =>
        new(
            new AdminViewModel(
                admin.Id,
                admin.Email ?? string.Empty,
                admin.Name,
                IdentityRoleNames.PlatformAdministrator),
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken,
            refreshExpiresAt.ToUnixTimeSeconds());

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static void EnsurePlatformAdministrator(IList<string> roles)
    {
        if (!roles.Contains(IdentityRoleNames.PlatformAdministrator, StringComparer.Ordinal))
            throw new AdminAuthenticationException("Administrator account was not found.");
    }
}
