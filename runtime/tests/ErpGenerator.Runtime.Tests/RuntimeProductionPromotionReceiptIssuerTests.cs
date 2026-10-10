using FluentAssertions;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeProductionPromotionReceiptIssuerTests
{
    private const string SigningKey = "promotion-receipt-signing-key-with-at-least-32-characters";

    [Fact]
    public void Issue_is_deterministic_and_verifiable_for_exact_promotion_lineage()
    {
        var issuer = CreateIssuer();
        var request = Request();

        var first = issuer.Issue(request, "42");
        var second = issuer.Issue(request, "42");

        first.Should().Be(second);
        first.ReceiptId.Should().Be(first.PayloadHash);
        first.SignatureAlgorithm.Should().Be("HMAC-SHA256");
        issuer.Verify(first).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_any_lineage_or_signature_change()
    {
        var issuer = CreateIssuer();
        var receipt = issuer.Issue(Request(), "42");

        issuer.Verify(receipt with { BundleHash = new string('b', 64) }).Should().BeFalse();
        issuer.Verify(receipt with { DeploymentId = "43" }).Should().BeFalse();
        issuer.Verify(receipt with { Signature = new string('0', 64) }).Should().BeFalse();
    }

    [Fact]
    public void Issue_requires_an_independent_strong_signing_key()
    {
        var issuer = new RuntimeProductionPromotionReceiptIssuer(
            Options.Create(new RuntimeDeploymentBrokerOptions
            {
                PromotionReceiptSigningKey = "too-short"
            }));

        var issue = () => issuer.Issue(Request(), "42");

        issue.Should().Throw<InvalidOperationException>()
            .WithMessage("*not configured safely*");
    }

    private static RuntimeProductionPromotionReceiptIssuer CreateIssuer() =>
        new(Options.Create(new RuntimeDeploymentBrokerOptions
        {
            PromotionReceiptSigningKey = SigningKey
        }));

    private static RuntimeProductionPromotionBrokerRequest Request() =>
        new(
            "operation-1",
            "run-1",
            "project-1",
            new RuntimeDeploymentTarget(
                "tenant-id",
                "tenant-stable-id",
                "tenant-staging.erpgenerator.example",
                "staging",
                "erp_tenant_stable_id_staging"),
            new RuntimeDeploymentTarget(
                "tenant-id",
                "tenant-stable-id",
                "tenant.erpgenerator.example",
                "production",
                "erp_tenant_stable_id_prod"),
            "bundle-id",
            "1.0.0",
            new string('a', 64),
            new string('c', 64));
}
