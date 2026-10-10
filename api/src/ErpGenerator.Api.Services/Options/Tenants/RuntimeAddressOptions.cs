namespace ErpGenerator.Api.Services.Options.Tenants;

public sealed class RuntimeAddressOptions
{
    public const string SectionName = "Runtime";
    public string PublicHostSuffix { get; set; } = "localhost:5001";
    public string Scheme { get; set; } = "http";
}
