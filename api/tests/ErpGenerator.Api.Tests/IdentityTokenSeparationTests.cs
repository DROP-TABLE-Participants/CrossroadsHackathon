using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ErpGenerator.Api.Tests;

public sealed class IdentityTokenSeparationTests
{
    [Fact]
    public void Customer_and_administrator_tokens_have_distinct_realms_audiences_and_signatures()
    {
        const string customerKey = "customer-signing-key-that-is-at-least-32-bytes";
        const string adminKey = "administrator-signing-key-that-is-at-least-32-bytes";
        var customerIssuer = new JwtAccessTokenIssuer(Options.Create(new JwtOptions
        {
            Issuer = "customer-issuer",
            Audience = "customer-audience",
            SigningKey = customerKey
        }));
        var adminIssuer = new AdminJwtAccessTokenIssuer(Options.Create(new AdminJwtOptions
        {
            Issuer = "admin-issuer",
            Audience = "admin-audience",
            SigningKey = adminKey
        }));
        var subject = new AccessTokenSubject(Guid.NewGuid(), "person@example.test", "security-stamp");

        var customerToken = customerIssuer.Issue(subject, ["Customer"]).Token;
        var adminToken = adminIssuer.Issue(subject, [IdentityRoleNames.PlatformAdministrator]).Token;
        var handler = new JwtSecurityTokenHandler();
        var customerJwt = handler.ReadJwtToken(customerToken);
        var adminJwt = handler.ReadJwtToken(adminToken);

        Assert.Equal(IdentityClaimNames.CustomerRealm, customerJwt.Claims.Single(
            value => value.Type == IdentityClaimNames.AccountRealm).Value);
        Assert.Equal(IdentityClaimNames.AdministratorRealm, adminJwt.Claims.Single(
            value => value.Type == IdentityClaimNames.AccountRealm).Value);
        Assert.Contains("customer-audience", customerJwt.Audiences);
        Assert.Contains("admin-audience", adminJwt.Audiences);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            customerToken,
            Validation("admin-issuer", "admin-audience", adminKey),
            out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            adminToken,
            Validation("customer-issuer", "customer-audience", customerKey),
            out _));
    }

    private static TokenValidationParameters Validation(string issuer, string audience, string key) =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            ClockSkew = TimeSpan.Zero
        };
}
