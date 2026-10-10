namespace ErpGenerator.Runtime.Services.Assistant;

public sealed class RuntimeAssistantOptions
{
    public const string SectionName = "RuntimeAssistant";
    public string Provider { get; set; } = "Disabled";
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public Uri Endpoint { get; set; } = new("https://api.anthropic.com/v1/messages");
    public int MaxOutputTokens { get; set; } = 3000;
    public int TimeoutSeconds { get; set; } = 120;
}
