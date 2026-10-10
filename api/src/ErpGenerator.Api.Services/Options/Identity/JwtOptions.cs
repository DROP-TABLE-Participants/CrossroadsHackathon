namespace ErpGenerator.Api.Services.Options.Identity;

public sealed record JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; init; } = "ErpGenerator.Api";
    public string Audience { get; init; } = "ErpGenerator.Portal";
    public string SigningKey { get; init; } = string.Empty;
    public int LifetimeMinutes { get; init; } = 60;
}
