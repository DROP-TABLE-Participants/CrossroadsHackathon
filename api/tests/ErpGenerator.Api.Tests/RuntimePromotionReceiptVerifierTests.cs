using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Tests;

public sealed class RuntimePromotionReceiptVerifierTests
{
    private const string VerificationKey = "receipt-verification-key-with-at-least-32-characters";

    [Fact]
    public void Valid_signature_is_accepted_and_tampering_is_rejected()
    {
        var payloadHash = new string('a', 64);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(VerificationKey),
            Encoding.UTF8.GetBytes(payloadHash)));
        var verifier = new RuntimePromotionReceiptVerifier(Options.Create(new RuntimeBrokerOptions
        {
            PromotionReceiptVerificationKey = VerificationKey
        }));

        Assert.True(verifier.IsConfigured);
        Assert.True(verifier.Verify(payloadHash, signature));
        Assert.False(verifier.Verify(new string('b', 64), signature));
        Assert.False(verifier.Verify(payloadHash, new string('0', 64)));
    }

    [Fact]
    public void Missing_or_weak_verification_key_fails_closed()
    {
        var verifier = new RuntimePromotionReceiptVerifier(Options.Create(new RuntimeBrokerOptions
        {
            PromotionReceiptVerificationKey = "too-short"
        }));

        Assert.False(verifier.IsConfigured);
        Assert.False(verifier.Verify(new string('a', 64), new string('b', 64)));
    }
}
