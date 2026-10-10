using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class RuntimeDeploymentProcessor(
    ErpGeneratorDbContext dbContext,
    IRuntimeDeploymentClient runtime) : IRuntimeDeploymentProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);
    private const int MaximumAttempts = 5;

    public async Task<bool> TryProcessNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await dbContext.Deployments
            .Where(value => value.Status == "deploying" && value.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, "pending")
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseId, (Guid?)null)
                .SetProperty(value => value.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);

        var candidate = await dbContext.Deployments.AsNoTracking()
            .Where(value => value.Status == "pending" && value.AttemptCount < MaximumAttempts)
            .OrderBy(value => value.CreatedAt)
            .Select(value => new { value.Id, value.Status, value.Environment })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;

        var leaseId = Guid.NewGuid();
        var activeStatus = "deploying";
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
            if (!result.Success || result.Status != "ready")
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
        deployment.Status = terminal ? "failed" : "pending";
        deployment.FailureCode = code ?? "deployment_failed";
        deployment.FailureMessage = message ?? "Runtime deployment failed.";
        environment.Status = terminal ? "failed" : "pending_deployment";
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
