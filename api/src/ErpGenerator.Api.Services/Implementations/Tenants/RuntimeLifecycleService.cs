using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class RuntimeLifecycleService(
    ErpGeneratorDbContext dbContext,
    IGenerationProjectService projects,
    IRuntimeDeploymentClient runtime,
    IOptions<RuntimeAddressOptions> runtimeAddresses) : IRuntimeLifecycleService
{
    public async Task<GenerationRunActionViewModel> RequestProductionPromotionAsync(
        Guid customerId,
        Guid runId,
        bool confirm,
        CancellationToken cancellationToken)
    {
        if (!confirm) throw new ArgumentException("Production promotion requires explicit confirmation.");
        var bundle = await dbContext.ErpBundleVersions
            .Include(value => value.GenerationRun).ThenInclude(value => value.GenerationProject).ThenInclude(value => value.Customer)
            .Include(value => value.Deployments).ThenInclude(value => value.Tenant).ThenInclude(value => value.Environments)
            .Where(value => value.GenerationRunId == runId && value.GenerationRun.GenerationProject.CustomerId == customerId)
            .OrderByDescending(value => value.Version)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new KeyNotFoundException("Generated ERP bundle was not found.");
        var stagingDeployment = bundle.Deployments.Where(value => value.Environment == "staging")
            .OrderByDescending(value => value.CreatedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("Deploy the ERP to staging before production promotion.");
        if (stagingDeployment.Status != "ready" || stagingDeployment.TestsStatus != "passed" ||
            string.IsNullOrWhiteSpace(stagingDeployment.TestSuiteHash) ||
            !IsSha256(stagingDeployment.DeployedBundleHash))
            throw new InvalidOperationException("Staging acceptance tests must pass before production promotion.");

        var tenant = stagingDeployment.Tenant;
        var production = tenant.Environments.SingleOrDefault(value => value.Environment == "production");
        if (production is null)
        {
            var suffix = runtimeAddresses.Value.ProductionPublicHostSuffix.TrimStart('.');
            var authority = $"{tenant.Slug}.{suffix}";
            var databaseName = TenantDatabaseNamePolicy.BuildExpected(tenant.StableId, "production");
            production = new TenantEnvironment
            {
                TenantId = tenant.Id,
                Environment = "production",
                Host = authority.Split(':')[0].ToLowerInvariant(),
                DatabaseName = databaseName,
                CredentialSecretReference = $"tenant-db:{tenant.StableId}:production:{databaseName}",
                RuntimeUrl = $"{runtimeAddresses.Value.Scheme}://{authority}",
                Status = "pending_promotion"
            };
            tenant.Environments.Add(production);
            dbContext.TenantEnvironments.Add(production);
        }

        var operationId = OperationId("production", tenant.StableId, bundle.ContentHash);
        var deployment = await dbContext.Deployments.SingleOrDefaultAsync(
            value => value.OperationId == operationId, cancellationToken);
        if (deployment is null)
        {
            deployment = new Deployment
            {
                TenantId = tenant.Id,
                ErpBundleVersionId = bundle.Id,
                Environment = "production",
                Status = "pending_promotion",
                OperationId = operationId
            };
            dbContext.Deployments.Add(deployment);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return new GenerationRunActionViewModel(await ReloadRunAsync(customerId, bundle.GenerationRun.GenerationProjectId, runId, cancellationToken));
    }

    public async Task<OneTimeActivationLinkViewModel> CreateActivationLinkAsync(
        Guid customerId,
        Guid runId,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = environment.Trim().ToLowerInvariant();
        if (normalizedEnvironment is not ("staging" or "production"))
            throw new ArgumentException("Environment must be staging or production.");
        var deployment = await dbContext.Deployments
            .Include(value => value.Tenant).ThenInclude(value => value.Environments)
            .Include(value => value.ErpBundleVersion).ThenInclude(value => value.GenerationRun)
                .ThenInclude(value => value.GenerationProject).ThenInclude(value => value.Customer)
            .Where(value => value.ErpBundleVersion.GenerationRunId == runId &&
                            value.ErpBundleVersion.GenerationRun.GenerationProject.CustomerId == customerId &&
                            value.Environment == normalizedEnvironment && value.Status == "ready")
            .OrderByDescending(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The requested ERP environment is not ready.");
        var target = deployment.Tenant.Environments.Single(value => value.Environment == normalizedEnvironment);
        var project = deployment.ErpBundleVersion.GenerationRun.GenerationProject;
        var email = project.OwnerEmail ?? project.Customer.Email
            ?? throw new InvalidOperationException("ERP owner email is missing.");
        var operationId = OperationId("activation", runId.ToString(), normalizedEnvironment, Guid.NewGuid().ToString("N"));
        var result = await runtime.CreateActivationAsync(new RuntimeOwnerActivationInputModel(
            operationId,
            runId.ToString(),
            project.Id.ToString(),
            new RuntimeDeploymentTargetViewModel(
                deployment.TenantId.ToString(), deployment.Tenant.StableId, target.Host,
                target.Environment, target.DatabaseName),
            email), cancellationToken);
        if (!result.Success || result.Activation is null)
            throw new InvalidOperationException(result.SafeError ?? "Runtime activation link could not be created.");

        target.OwnerActivationUrl = result.Activation.Url;
        target.OwnerActivationExpiresAt = result.Activation.ExpiresAt;
        var normalizedEmail = result.Activation.Email.Trim().ToLowerInvariant();
        var activation = await dbContext.OwnerActivations.SingleOrDefaultAsync(
            value => value.TenantEnvironmentId == target.Id && value.Email == normalizedEmail, cancellationToken);
        if (activation is null)
        {
            activation = new OwnerActivation { TenantEnvironmentId = target.Id, Email = normalizedEmail };
            dbContext.OwnerActivations.Add(activation);
        }
        activation.Status = "pending";
        await dbContext.SaveChangesAsync(cancellationToken);
        return new OneTimeActivationLinkViewModel(
            normalizedEnvironment, "pending", result.Activation.Url,
            result.Activation.Email, result.Activation.ExpiresAt);
    }

    private async Task<GenerationRunViewModel> ReloadRunAsync(
        Guid customerId, Guid projectId, Guid runId, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(customerId, projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Generation project was not found.");
        return project.GenerationRuns.Single(value => value.Id == runId);
    }

    private static string OperationId(params string[] values)
    {
        var canonical = string.Join('\0', values.Select(value => value.Trim()));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
