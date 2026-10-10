namespace ErpGenerator.Api.Services.Options.Generation;

public sealed class InterviewEmailOptions
{
    public const string SectionName = "InterviewEmail";
    public string Mode { get; set; } = "Disabled";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "ERP Generator";
}
