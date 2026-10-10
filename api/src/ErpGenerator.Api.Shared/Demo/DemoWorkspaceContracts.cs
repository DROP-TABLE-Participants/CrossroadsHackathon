namespace ErpGenerator.Api.Shared.Demo;

public sealed record DemoTemplateViewModel(string Version, string Name, string Description, string SourceUrl);
public sealed record DemoInstanceViewModel(Guid Id, string TemplateVersion, string Status, string Name, string? RuntimeUrl, string? FailureMessage, DateTimeOffset CreatedAt);
public sealed record CreateFromTemplateInputModel(string TemplateVersion, IReadOnlyList<string>? Choices = null, string? Prompt = null);
