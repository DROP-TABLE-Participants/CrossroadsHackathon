namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface IRuntimePromotionReceiptVerifier
{
    bool IsConfigured { get; }
    bool Verify(string? payloadHash, string? signature);
}
