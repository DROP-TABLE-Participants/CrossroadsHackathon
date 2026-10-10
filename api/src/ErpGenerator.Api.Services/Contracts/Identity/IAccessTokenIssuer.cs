namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IAccessTokenIssuer
{
    AccessTokenViewModel Issue(AccessTokenSubject subject, IReadOnlyCollection<string> roles);
}
