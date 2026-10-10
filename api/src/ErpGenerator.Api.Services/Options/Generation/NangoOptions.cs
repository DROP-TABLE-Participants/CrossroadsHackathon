namespace ErpGenerator.Api.Services.Options.Generation;

public sealed class NangoOptions
{
    public const string SectionName = "Nango";
    public string BaseUrl { get; set; } = "https://api.nango.dev";
    public string ConnectBaseUrl { get; set; } = "https://connect.nango.dev";
    public string SecretKey { get; set; } = string.Empty;
    public string Environment { get; set; } = "dev";
    public string AllowedFileIntegrations { get; set; } = string.Empty;
    public string ConnectionWebhookUrl { get; set; } = string.Empty;
    public string WebhookSigningKey { get; set; } = string.Empty;
    public int ConnectorContentRetentionDays { get; set; } = 30;
}
