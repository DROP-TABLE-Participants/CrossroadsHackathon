namespace ErpGenerator.Api.Shared.Identity;

public sealed record CustomerAuthenticationViewModel(
    CustomerViewModel User,
    string AccessToken,
    long AccessTokenExpiresAt,
    string RefreshToken,
    long RefreshTokenExpiresAt);
