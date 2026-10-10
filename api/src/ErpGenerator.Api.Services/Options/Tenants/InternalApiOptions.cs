namespace ErpGenerator.Api.Services.Options.Tenants;

public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";
    public string Secret { get; set; } = string.Empty;
    public string DatabaseSecretEncryptionKey { get; set; } = string.Empty;
}
