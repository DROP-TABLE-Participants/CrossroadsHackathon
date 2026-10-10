namespace ErpGenerator.Api.Services.Implementations.Tenants;

internal sealed record EncryptedPayload(
    string Ciphertext,
    string Nonce,
    string Tag);
