namespace ErpGenerator.Api.Services.Implementations.Tenants;

internal sealed record StoredCredential(
    string Username,
    string Host,
    int Port,
    string Database,
    string Password,
    string SslMode);
