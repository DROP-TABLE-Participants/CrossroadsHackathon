namespace ErpGenerator.Api.Services.Options.Tenants;

public sealed class RuntimeBrokerOptions
{
    public const string SectionName = "RuntimeBroker";
    public string BaseUrl { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string PromotionReceiptVerificationKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 300;
}
