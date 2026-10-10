namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IAdminAccessTokenIssuer
{
    AccessTokenViewModel Issue(AccessTokenSubject subject, IReadOnlyCollection<string> roles);
}
