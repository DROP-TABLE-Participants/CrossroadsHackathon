namespace ErpGenerator.Api.Services.Options.Identity;

public sealed record AdminJwtOptions
{
    public const string SectionName = "AdminJwt";
    public string Issuer { get; init; } = "ErpGenerator.Api.Administration";
    public string Audience { get; init; } = "ErpGenerator.Portal.Administration";
    public string SigningKey { get; init; } = string.Empty;
    public int LifetimeMinutes { get; init; } = 30;
}
