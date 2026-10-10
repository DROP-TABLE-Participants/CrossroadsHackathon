namespace ErpGenerator.Api.Services.Options.Identity;

public sealed class CustomerSessionOptions
{
    public const string SectionName = "CustomerSession";
    public int RefreshLifetimeDays { get; set; } = 7;
}
