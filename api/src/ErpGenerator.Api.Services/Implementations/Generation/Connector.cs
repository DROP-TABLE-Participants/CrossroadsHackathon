namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record Connector(
    string Key,
    string DisplayName,
    string SourceFamily,
    IReadOnlyList<string> ProviderConfigKeys);
