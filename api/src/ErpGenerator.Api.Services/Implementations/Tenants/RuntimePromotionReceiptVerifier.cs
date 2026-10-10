using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class RuntimePromotionReceiptVerifier(IOptions<RuntimeBrokerOptions> options)
    : IRuntimePromotionReceiptVerifier
{
    public bool IsConfigured => options.Value.PromotionReceiptVerificationKey?.Trim().Length >= 32;

    public bool Verify(string? payloadHash, string? signature)
    {
        var key = options.Value.PromotionReceiptVerificationKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32 ||
            !IsSha256(payloadHash) || !IsSha256(signature))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(payloadHash!.ToLowerInvariant()));
        var actual = Convert.FromHexString(signature!);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
