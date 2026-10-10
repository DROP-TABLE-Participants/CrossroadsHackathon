using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class RuntimeDeploymentProcessor(
    ErpGeneratorDbContext dbContext,
    IRuntimeDeploymentClient runtime,
    IRuntimePromotionReceiptVerifier receiptVerifier) : IRuntimeDeploymentProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);
    private const int MaximumAttempts = 5;

    public async Task<bool> TryProcessNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await dbContext.Deployments
            .Where(value => (value.Status == "deploying" || value.Status == "promoting") && value.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, value => value.Environment == "production" ? "pending_promotion" : "pending")
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseId, (Guid?)null)
                .SetProperty(value => value.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);

        var candidate = await dbContext.Deployments.AsNoTracking()
            .Where(value => (value.Status == "pending" || value.Status == "pending_promotion") && value.AttemptCount < MaximumAttempts)
            .OrderBy(value => value.CreatedAt)
            .Select(value => new { value.Id, value.Status, value.Environment })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;

        var leaseId = Guid.NewGuid();
        var activeStatus = candidate.Environment == "production" ? "promoting" : "deploying";
        var claimed = await dbContext.Deployments
            .Where(value => value.Id == candidate.Id && value.Status == candidate.Status)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, activeStatus)
                .SetProperty(value => value.LeaseOwner, workerId)
                .SetProperty(value => value.LeaseId, leaseId)
                .SetProperty(value => value.LeaseExpiresAt, now.Add(LeaseDuration))
                .SetProperty(value => value.AttemptCount, value => value.AttemptCount + 1), cancellationToken);
        if (claimed == 0) return true;

        dbContext.ChangeTracker.Clear();
        var deployment = await dbContext.Deployments
            .Include(value => value.Tenant).ThenInclude(value => value.Environments)
            .Include(value => value.ErpBundleVersion)
                .ThenInclude(value => value.GenerationArtifactVersion)
            .Include(value => value.ErpBundleVersion).ThenInclude(value => value.GenerationRun)
                .ThenInclude(value => value.GenerationProject).ThenInclude(value => value.Customer)
            .SingleAsync(value => value.Id == candidate.Id && value.LeaseId == leaseId, cancellationToken);
        var environment = deployment.Tenant.Environments.SingleOrDefault(value => value.Environment == deployment.Environment)
            ?? throw new InvalidOperationException("Deployment target environment is missing.");

        try
        {
            if (deployment.Environment == "production")
            {
                await PromoteProductionAsync(deployment, environment, cancellationToken);
                return true;
            }
            var envelope = JsonSerializer.Deserialize<AgentOutputEnvelope>(
                deployment.ErpBundleVersion.GenerationArtifactVersion.CanonicalJson,
                JsonOptions)
                ?? throw new InvalidOperationException("Canonical ERP artifact envelope is invalid.");
            var bundle = envelope.Payload.Deserialize<GeneratedErpBundle>(JsonOptions)
                ?? throw new InvalidOperationException("Canonical ERP bundle is invalid.");
            var input = new CanonicalRuntimeDeploymentInputModel(
                deployment.OperationId,
                deployment.ErpBundleVersion.GenerationRunId.ToString(),
                deployment.ErpBundleVersion.GenerationRun.GenerationProjectId.ToString(),
                new RuntimeDeploymentTargetViewModel(
                    deployment.TenantId.ToString(), deployment.Tenant.StableId, environment.Host,
                    environment.Environment, environment.DatabaseName),
                deployment.ErpBundleVersion.ContentHash,
                bundle,
                deployment.ErpBundleVersion.GenerationRun.GenerationProject.OwnerEmail ??
                    deployment.ErpBundleVersion.GenerationRun.GenerationProject.Customer.Email);
            var result = await runtime.DeployCanonicalAsync(input, cancellationToken);
            if (!result.Success)
            {
                await MarkFailureAsync(deployment, environment, result.FailureCode, result.SafeError, cancellationToken);
                return true;
            }
            if (!EqualsIgnoreCase(
                    result.ArtifactHash,
                    deployment.ErpBundleVersion.ContentHash) ||
                !IsSha256(result.BundleHash) ||
                result.TestsStatus != "passed" ||
                !IsSha256(result.TestSuiteHash))
            {
                await MarkFailureAsync(
                    deployment,
                    environment,
                    "runtime_bundle_lineage_mismatch",
                    "Runtime deployment did not prove tests against the selected ERP bundle.",
                    cancellationToken);
                return true;
            }

            deployment.Status = result.Status;
            deployment.RuntimeDeploymentId = result.DeploymentId;
            deployment.DeployedBundleId = result.BundleId;
            deployment.DeployedBundleVersion = result.BundleVersion;
            deployment.DeployedBundleHash = result.BundleHash;
            deployment.TestsStatus = result.TestsStatus;
            deployment.TestSuiteHash = result.TestSuiteHash;
            deployment.FailureCode = null;
            deployment.FailureMessage = null;
            ClearLease(deployment);
            environment.Status = result.Status;
            environment.RuntimeUrl = result.RuntimeUrl;
            environment.ActiveBundleId = result.BundleId;
            environment.ActiveBundleVersion = result.BundleVersion;
            environment.ActiveBundleHash = result.BundleHash;
            if (result.Activation is not null)
            {
                environment.OwnerActivationUrl = result.Activation.Url;
                environment.OwnerActivationExpiresAt = result.Activation.ExpiresAt;
                await UpsertActivationAsync(environment.Id, result.Activation.Email, cancellationToken);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkFailureAsync(deployment, environment, "runtime_unavailable", "Runtime deployment is temporarily unavailable.", cancellationToken);
        }
        return true;
    }

    private async Task PromoteProductionAsync(
        Deployment deployment,
        TenantEnvironment production,
        CancellationToken cancellationToken)
    {
        if (!receiptVerifier.IsConfigured)
        {
            await MarkFailureAsync(
                deployment,
                production,
                "promotion_receipt_verification_unavailable",
                "Production promotion receipt verification is not configured.",
                cancellationToken);
            return;
        }

        var staging = deployment.Tenant.Environments.SingleOrDefault(value => value.Environment == "staging")
            ?? throw new InvalidOperationException("Staging environment is missing.");
        var stagedDeployment = await dbContext.Deployments.AsNoTracking()
            .Where(value => value.TenantId == deployment.TenantId &&
                            value.ErpBundleVersionId == deployment.ErpBundleVersionId &&
                            value.Environment == "staging" && value.Status == "ready")
            .OrderByDescending(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A passing staging deployment is required for promotion.");
        if (string.IsNullOrWhiteSpace(stagedDeployment.DeployedBundleId) ||
        string.IsNullOrWhiteSpace(stagedDeployment.DeployedBundleVersion) ||
        !IsSha256(stagedDeployment.DeployedBundleHash) ||
        !IsSha256(stagedDeployment.TestSuiteHash) ||
        stagedDeployment.TestsStatus != "passed")
            throw new InvalidOperationException("Staging deployment lineage is incomplete.");

        var run = deployment.ErpBundleVersion.GenerationRun;
        var project = run.GenerationProject;
        var result = await runtime.PromoteProductionAsync(new RuntimeProductionPromotionInputModel(
            deployment.OperationId,
            run.Id.ToString(),
            project.Id.ToString(),
            Target(deployment.TenantId, deployment.Tenant.StableId, staging),
            Target(deployment.TenantId, deployment.Tenant.StableId, production),
            stagedDeployment.DeployedBundleId,
            stagedDeployment.DeployedBundleVersion,
            stagedDeployment.DeployedBundleHash!,
            stagedDeployment.TestSuiteHash!,
            production.ActiveBundleVersion,
            production.ActiveBundleHash,
            project.OwnerEmail ?? project.Customer.Email), cancellationToken);
        if (!result.Success)
        {
            await MarkFailureAsync(deployment, production, result.FailureCode, result.SafeError, cancellationToken);
            return;
        }

        if (!TryValidatePromotionReceipt(
                result,
                deployment,
                staging,
                production,
                stagedDeployment,
                out var receiptJson))
        {
            await MarkFailureAsync(
                deployment,
                production,
                "invalid_promotion_receipt",
                "Runtime returned an invalid production promotion receipt.",
                cancellationToken);
            return;
        }

        deployment.Status = result.Status;
        deployment.RuntimeDeploymentId = result.DeploymentId;
        deployment.DeployedBundleId = result.BundleId;
        deployment.DeployedBundleVersion = result.BundleVersion;
        deployment.DeployedBundleHash = result.BundleHash;
        deployment.TestSuiteHash = result.TestSuiteHash;
        deployment.TestsStatus = "passed";
        deployment.PromotionReceiptId = result.PromotionReceipt!.ReceiptId;
        deployment.PromotionReceiptPayloadHash = result.PromotionReceipt.PayloadHash;
        deployment.PromotionReceiptSignature = result.PromotionReceipt.Signature;
        deployment.PromotionReceiptJson = receiptJson;
        deployment.FailureCode = null;
        deployment.FailureMessage = null;
        ClearLease(deployment);
        production.Status = result.Status;
        production.RuntimeUrl = result.RuntimeUrl;
        production.ActiveBundleId = result.BundleId;
        production.ActiveBundleVersion = result.BundleVersion;
        production.ActiveBundleHash = result.BundleHash;
        if (result.Activation is not null)
        {
            production.OwnerActivationUrl = result.Activation.Url;
            production.OwnerActivationExpiresAt = result.Activation.ExpiresAt;
            await UpsertActivationAsync(production.Id, result.Activation.Email, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private bool TryValidatePromotionReceipt(
        RuntimeProductionPromotionViewModel result,
        Deployment deployment,
        TenantEnvironment staging,
        TenantEnvironment production,
        Deployment stagedDeployment,
        out string? receiptJson)
    {
        receiptJson = null;
        var receipt = result.PromotionReceipt;
        var run = deployment.ErpBundleVersion.GenerationRun;
        var project = run.GenerationProject;
        if (receipt is null ||
            receipt.ReceiptVersion != "1" ||
            receipt.SignatureAlgorithm != "HMAC-SHA256" ||
            receipt.OperationId != deployment.OperationId ||
            receipt.RunId != run.Id.ToString() ||
            receipt.GenerationProjectId != project.Id.ToString() ||
            receipt.TenantStableId != deployment.Tenant.StableId ||
            receipt.BundleId != stagedDeployment.DeployedBundleId ||
            receipt.BundleVersion != stagedDeployment.DeployedBundleVersion ||
            !EqualsIgnoreCase(receipt.BundleHash, stagedDeployment.DeployedBundleHash) ||
            !EqualsIgnoreCase(receipt.TestSuiteHash, stagedDeployment.TestSuiteHash) ||
            string.IsNullOrWhiteSpace(receipt.DeploymentId) ||
            receipt.DeploymentId != result.DeploymentId ||
            receipt.BundleId != result.BundleId ||
            receipt.BundleVersion != result.BundleVersion ||
            !EqualsIgnoreCase(receipt.BundleHash, result.BundleHash) ||
            !EqualsIgnoreCase(receipt.TestSuiteHash, result.TestSuiteHash) ||
            !IsSha256(receipt.ReceiptId) ||
            !IsSha256(receipt.PayloadHash) ||
            !IsSha256(receipt.Signature) ||
            !EqualsIgnoreCase(receipt.ReceiptId, receipt.PayloadHash) ||
            !receiptVerifier.Verify(receipt.PayloadHash, receipt.Signature) ||
            receipt.StagingEnvironmentIdentity != ComputeEnvironmentIdentity(deployment.Tenant.StableId, staging) ||
            receipt.ProductionEnvironmentIdentity != ComputeEnvironmentIdentity(deployment.Tenant.StableId, production))
        {
            return false;
        }

        var canonical = CanonicalizeReceipt(receipt);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        if (!EqualsIgnoreCase(expectedHash, receipt.PayloadHash))
        {
            return false;
        }

        receiptJson = JsonSerializer.Serialize(receipt, JsonOptions);
        return true;
    }

    private static string CanonicalizeReceipt(RuntimeProductionPromotionReceiptViewModel receipt)
    {
        var values = new[]
        {
            receipt.OperationId,
            receipt.RunId,
            receipt.GenerationProjectId,
            receipt.TenantStableId,
            receipt.StagingEnvironmentIdentity,
            receipt.ProductionEnvironmentIdentity,
            receipt.BundleId,
            receipt.BundleVersion,
            receipt.BundleHash.ToLowerInvariant(),
            receipt.TestSuiteHash.ToLowerInvariant(),
            receipt.DeploymentId
        };
        var names = new[]
        {
            "operationId", "runId", "generationProjectId", "tenantStableId",
            "stagingEnvironmentIdentity", "productionEnvironmentIdentity", "bundleId",
            "bundleVersion", "bundleHash", "testSuiteHash", "deploymentId"
        };
        var canonical = new StringBuilder();
        AppendCanonical(canonical, "receiptVersion", "1");
        for (var index = 0; index < names.Length; index++)
        {
            AppendCanonical(canonical, names[index], values[index]);
        }

        return canonical.ToString();
    }

    private static void AppendCanonical(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append('=').Append(value.Length).Append(':').Append(value).Append('\n');

    private static string ComputeEnvironmentIdentity(string stableId, TenantEnvironment environment)
    {
        var canonical = string.Join(
            '\n',
            stableId.Trim(),
            environment.Environment.Trim().ToLowerInvariant(),
            environment.DatabaseName.Trim().ToLowerInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool EqualsIgnoreCase(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private async Task MarkFailureAsync(
        Deployment deployment,
        TenantEnvironment environment,
        string? code,
        string? message,
        CancellationToken cancellationToken)
    {
        var terminal = deployment.AttemptCount >= MaximumAttempts;
        deployment.Status = terminal ? "failed" : deployment.Environment == "production" ? "pending_promotion" : "pending";
        deployment.FailureCode = code ?? "deployment_failed";
        deployment.FailureMessage = message ?? "Runtime deployment failed.";
        environment.Status = terminal ? "failed" : deployment.Environment == "production" ? "pending_promotion" : "pending_deployment";
        ClearLease(deployment);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertActivationAsync(Guid environmentId, string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var activation = await dbContext.OwnerActivations.SingleOrDefaultAsync(
            value => value.TenantEnvironmentId == environmentId && value.Email == normalized, cancellationToken);
        if (activation is null)
        {
            activation = new OwnerActivation
            {
                TenantEnvironmentId = environmentId,
                Email = normalized
            };
            dbContext.OwnerActivations.Add(activation);
        }
        activation.Status = "pending";
    }

    private static void ClearLease(Deployment deployment)
    {
        deployment.LeaseOwner = null;
        deployment.LeaseId = null;
        deployment.LeaseExpiresAt = null;
    }

    private static RuntimeDeploymentTargetViewModel Target(
        Guid tenantId,
        string stableId,
        TenantEnvironment environment) =>
        new(tenantId.ToString(), stableId, environment.Host, environment.Environment, environment.DatabaseName);
}
