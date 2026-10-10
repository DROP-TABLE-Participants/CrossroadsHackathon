namespace ErpGenerator.Api.Services.Options.Demo;

public sealed class DemoNotificationOptions
{
    public const string SectionName = "DemoNotifications";
    public string AdminRecipient { get; set; } = string.Empty;
}
