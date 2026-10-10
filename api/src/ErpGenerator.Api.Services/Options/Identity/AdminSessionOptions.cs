namespace ErpGenerator.Api.Services.Options.Identity;

public sealed class AdminSessionOptions
{
    public const string SectionName = "AdminSession";
    public int RefreshLifetimeDays { get; set; } = 1;
}
