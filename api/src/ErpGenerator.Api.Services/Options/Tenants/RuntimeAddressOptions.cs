namespace ErpGenerator.Api.Services.Options.Tenants;

public sealed class RuntimeAddressOptions
{
    public const string SectionName = "Runtime";
    public string PublicHostSuffix { get; set; } = "localhost:5001";
    public string Scheme { get; set; } = "http";
    public string ReservedTenantSlugs { get; set; } = string.Empty;

    public bool IsReserved(string slug) => ReservedTenantSlugs
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains(slug, StringComparer.OrdinalIgnoreCase);
}
