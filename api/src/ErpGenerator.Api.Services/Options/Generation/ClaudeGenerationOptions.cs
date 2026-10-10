namespace ErpGenerator.Api.Services.Options.Generation;

public sealed class ClaudeGenerationOptions
{
    public const string SectionName = "Claude";
    public string Provider { get; set; } = "Claude";
    public string? Model { get; set; } = "claude-opus-5-5";
    public string? ApiKey { get; set; }
    public Uri Endpoint { get; set; } = new("https://api.anthropic.com/v1/messages");
    public string ApiVersion { get; set; } = "2023-06-01";
    public string Effort { get; set; } = "medium";
    public int MaxOutputTokens { get; set; } = 30_000;
    public int TimeoutSeconds { get; set; } = 1_200;
}
