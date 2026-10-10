using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ErpGenerator.Api.Tests;

public sealed class CustomerAccessTokenTests
{
    [Fact]
    public void Customer_tokens_include_the_customer_realm_and_reject_wrong_validation_parameters()
    {
        const string customerKey = "customer-signing-key-that-is-at-least-32-bytes";
        var customerIssuer = new JwtAccessTokenIssuer(Options.Create(new JwtOptions
        {
            Issuer = "customer-issuer",
            Audience = "customer-audience",
            SigningKey = customerKey
        }));
        var subject = new AccessTokenSubject(Guid.NewGuid(), "person@example.test", "security-stamp");

        var customerToken = customerIssuer.Issue(subject, ["Customer"]).Token;
        var handler = new JwtSecurityTokenHandler();
        var customerJwt = handler.ReadJwtToken(customerToken);

        Assert.Equal(IdentityClaimNames.CustomerRealm, customerJwt.Claims.Single(
            value => value.Type == IdentityClaimNames.AccountRealm).Value);
        Assert.Contains("customer-audience", customerJwt.Audiences);
        handler.ValidateToken(
            customerToken,
            Validation("customer-issuer", "customer-audience", customerKey),
            out _);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            customerToken,
            Validation("wrong-issuer", "wrong-audience", "wrong-signing-key-that-is-at-least-32-bytes"),
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
