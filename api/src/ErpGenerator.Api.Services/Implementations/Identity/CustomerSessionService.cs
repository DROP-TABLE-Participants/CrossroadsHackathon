using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class CustomerSessionService(
    ErpGeneratorDbContext dbContext,
    UserManager<CustomerAccount> userManager,
    IAccessTokenIssuer accessTokenIssuer,
    IOptions<CustomerSessionOptions> options) : ICustomerSessionService
{
    public async Task<CustomerAuthenticationViewModel> CreateAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var customer = await dbContext.CustomerAccounts.SingleOrDefaultAsync(
            value => value.Id == customerId,
            cancellationToken);
        if (customer is null || !customer.IsActive)
            throw new CustomerAuthenticationException("Customer account was not found.");

        var roles = await userManager.GetRolesAsync(customer);
        var accessToken = accessTokenIssuer.Issue(Subject(customer), roles.ToArray());
        var refreshToken = CreateToken();
        var refreshExpiresAt = DateTimeOffset.UtcNow.AddDays(Math.Clamp(options.Value.RefreshLifetimeDays, 1, 90));
        dbContext.RefreshSessions.Add(new RefreshSession
        {
            CustomerAccountId = customer.Id,
            TokenHash = Hash(refreshToken),
            ExpiresAt = refreshExpiresAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Authentication(customer, roles, accessToken, refreshToken, refreshExpiresAt);
    }

    public async Task<CustomerAuthenticationViewModel> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new CustomerAuthenticationException("Invalid session.");
        var tokenHash = Hash(refreshToken);
        var session = await dbContext.RefreshSessions
            .Include(value => value.CustomerAccount)
            .SingleOrDefaultAsync(value => value.TokenHash == tokenHash, cancellationToken)
            ?? throw new CustomerAuthenticationException("Invalid session.");

        var now = DateTimeOffset.UtcNow;
        if (session.RevokedAt is not null)
        {
            if (session.ReplacedByTokenHash is not null)
            {
                var activeSessions = await dbContext.RefreshSessions
                    .Where(value => value.CustomerAccountId == session.CustomerAccountId && value.RevokedAt == null)
                    .ToListAsync(cancellationToken);
                foreach (var activeSession in activeSessions) activeSession.RevokedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            throw new CustomerAuthenticationException("Invalid session.");
        }
        if (session.ExpiresAt <= now || !session.CustomerAccount.IsActive)
        {
            session.RevokedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new CustomerAuthenticationException("Invalid session.");
        }

        var replacementToken = CreateToken();
        var replacementHash = Hash(replacementToken);
        var replacementExpiresAt = now.AddDays(Math.Clamp(options.Value.RefreshLifetimeDays, 1, 90));
        session.RevokedAt = now;
        session.ReplacedByTokenHash = replacementHash;
        dbContext.RefreshSessions.Add(new RefreshSession
        {
            CustomerAccountId = session.CustomerAccountId,
            TokenHash = replacementHash,
            ExpiresAt = replacementExpiresAt
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CustomerAuthenticationException("Invalid session.");
        }

        var roles = await userManager.GetRolesAsync(session.CustomerAccount);
        var accessToken = accessTokenIssuer.Issue(Subject(session.CustomerAccount), roles.ToArray());
        return Authentication(session.CustomerAccount, roles, accessToken, replacementToken, replacementExpiresAt);
    }

    public async Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var tokenHash = Hash(refreshToken);
        var session = await dbContext.RefreshSessions.SingleOrDefaultAsync(
            value => value.TokenHash == tokenHash,
            cancellationToken);
        if (session is null || session.RevokedAt is not null) return;
        session.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var sessions = await dbContext.RefreshSessions
            .Where(value => value.CustomerAccountId == customerId && value.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var session in sessions) session.RevokedAt = now;
        if (sessions.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static CustomerAuthenticationViewModel Authentication(
        CustomerAccount customer,
        IList<string> roles,
        AccessTokenViewModel accessToken,
        string refreshToken,
        DateTimeOffset refreshExpiresAt)
    {
        var role = roles.FirstOrDefault() ?? "Customer";
        return new CustomerAuthenticationViewModel(
            CustomerViewModelFactory.Create(customer, role),
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken,
            refreshExpiresAt.ToUnixTimeSeconds());
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static AccessTokenSubject Subject(CustomerAccount customer) =>
        new(customer.Id, customer.Email, customer.SecurityStamp ?? string.Empty);
}
