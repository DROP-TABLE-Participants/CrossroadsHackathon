using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public sealed class RuntimeProductionPromotionReceiptIssuer(
    IOptions<RuntimeDeploymentBrokerOptions> options)
{
    public const string ReceiptVersion = "1";
    public const string SignatureAlgorithm = "HMAC-SHA256";

    public void EnsureConfigured() => _ = GetSigningKey();

    public RuntimeProductionPromotionReceipt Issue(
        RuntimeProductionPromotionBrokerRequest request,
        string deploymentId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        var stagingIdentity = RuntimeLifecyclePrivilegePromotionAuthorization.ComputeEnvironmentIdentity(
            request.StagingTarget);
        var productionIdentity = RuntimeLifecyclePrivilegePromotionAuthorization.ComputeEnvironmentIdentity(
            request.ProductionTarget);
        var canonical = Canonicalize(
            request,
            deploymentId,
            stagingIdentity,
            productionIdentity);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var signature = ComputeSignature(payloadHash, GetSigningKey());

        return new RuntimeProductionPromotionReceipt(
            ReceiptVersion,
            payloadHash,
            request.OperationId,
            request.RunId,
            request.GenerationProjectId,
            request.ProductionTarget.TenantStableId,
            stagingIdentity,
            productionIdentity,
            request.BundleId,
            request.BundleVersion,
            request.BundleHash.ToLowerInvariant(),
            request.TestSuiteHash.ToLowerInvariant(),
            deploymentId,
            payloadHash,
            SignatureAlgorithm,
            signature);
    }

    public bool Verify(RuntimeProductionPromotionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(receipt.ReceiptVersion, ReceiptVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.SignatureAlgorithm, SignatureAlgorithm, StringComparison.Ordinal) ||
            !string.Equals(receipt.ReceiptId, receipt.PayloadHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var canonical = Canonicalize(receipt);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        if (!FixedTimeEquals(expectedHash, receipt.PayloadHash))
        {
            return false;
        }

        return FixedTimeEquals(
            ComputeSignature(expectedHash, GetSigningKey()),
            receipt.Signature);
    }

    private string GetSigningKey()
    {
        var value = options.Value.PromotionReceiptSigningKey?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
        {
            throw new InvalidOperationException(
                "Runtime production promotion receipt signing is not configured safely.");
        }

        return value;
    }

    private static string Canonicalize(
        RuntimeProductionPromotionBrokerRequest request,
        string deploymentId,
        string stagingIdentity,
        string productionIdentity) =>
        Canonicalize(
            request.OperationId,
            request.RunId,
            request.GenerationProjectId,
            request.ProductionTarget.TenantStableId,
            stagingIdentity,
            productionIdentity,
            request.BundleId,
            request.BundleVersion,
            request.BundleHash,
            request.TestSuiteHash,
            deploymentId);

    private static string Canonicalize(RuntimeProductionPromotionReceipt receipt) =>
        Canonicalize(
            receipt.OperationId,
            receipt.RunId,
            receipt.GenerationProjectId,
            receipt.TenantStableId,
            receipt.StagingEnvironmentIdentity,
            receipt.ProductionEnvironmentIdentity,
            receipt.BundleId,
            receipt.BundleVersion,
            receipt.BundleHash,
            receipt.TestSuiteHash,
            receipt.DeploymentId);

    private static string Canonicalize(params string[] values)
    {
        var canonical = new StringBuilder();
        Append(canonical, "receiptVersion", ReceiptVersion);
        var names = new[]
        {
            "operationId", "runId", "generationProjectId", "tenantStableId",
            "stagingEnvironmentIdentity", "productionEnvironmentIdentity", "bundleId",
            "bundleVersion", "bundleHash", "testSuiteHash", "deploymentId"
        };
        for (var index = 0; index < names.Length; index++)
        {
            var value = names[index] is "bundleHash" or "testSuiteHash"
                ? values[index].ToLowerInvariant()
                : values[index];
            Append(canonical, names[index], value);
        }

        return canonical.ToString();
    }

    private static void Append(StringBuilder builder, string name, string value) =>
        builder.Append(name)
            .Append('=')
            .Append(value.Length)
            .Append(':')
            .Append(value)
            .Append('\n');

    private static string ComputeSignature(string payloadHash, string signingKey)
    {
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey),
            Encoding.UTF8.GetBytes(payloadHash.ToLowerInvariant()));
        return Convert.ToHexStringLower(signature);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(right.ToLowerInvariant()));
    }
}
