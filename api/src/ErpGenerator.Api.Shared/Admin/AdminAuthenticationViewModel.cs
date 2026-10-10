namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminAuthenticationViewModel(
    AdminViewModel User,
    string AccessToken,
    long AccessTokenExpiresAt,
    string RefreshToken,
    long RefreshTokenExpiresAt);
