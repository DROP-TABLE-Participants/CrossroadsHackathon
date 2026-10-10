namespace ErpGenerator.Api.Shared.Generation;

public sealed record NangoConnectSessionViewModel(
    string SessionToken,
    string ConnectLink,
    string ExpiresAt,
    GenerationInputSourceViewModel InputSource);
