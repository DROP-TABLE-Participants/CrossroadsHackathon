using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options) : IAccessTokenIssuer
{
    public AccessTokenViewModel Issue(AccessTokenSubject subject, IReadOnlyCollection<string> roles)
    {
        var settings = options.Value;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(settings.LifetimeMinutes, 5, 24 * 60));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, subject.Id.ToString()),
                new Claim(ClaimTypes.Email, subject.Email ?? string.Empty),
                new Claim(IdentityClaimNames.AccountRealm, IdentityClaimNames.CustomerRealm),
                new Claim(IdentityClaimNames.SecurityStamp, subject.SecurityStamp),
                ..roles.Select(role => new Claim(ClaimTypes.Role, role))
            ],
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);
        return new AccessTokenViewModel(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt.ToUnixTimeSeconds());
    }
}
