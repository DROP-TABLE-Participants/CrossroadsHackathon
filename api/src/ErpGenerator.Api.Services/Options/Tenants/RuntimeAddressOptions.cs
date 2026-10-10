namespace ErpGenerator.Api.Services.Options.Tenants;

public sealed class RuntimeAddressOptions
{
    public const string SectionName = "Runtime";
    public string StagingPublicHostSuffix { get; set; } = "localhost:5001";
    public string ProductionPublicHostSuffix { get; set; } = "localhost:5002";
    public string Scheme { get; set; } = "http";
}
