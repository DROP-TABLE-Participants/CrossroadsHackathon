namespace ErpGenerator.Api.Services.Options.Identity;

public sealed class AdminBootstrapOptions
{
    public const string SectionName = "AdminBootstrap";
    public bool Enabled { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Name { get; set; } = "ERP Generator administrator";
}
