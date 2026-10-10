using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Tests;

public sealed class PostgresRuntimeDeploymentLineageTests
{
    [PostgresFact]
    public async Task Staging_rejects_a_runtime_result_for_a_different_artifact_hash()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var seeded = await SeedPendingDeploymentAsync(database.Services);
        var runtime = new StubRuntimeDeploymentClient(
            new string('b', 64),
            new string('c', 64),
            new string('d', 64));
        await using var scope = database.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
        var processor = new RuntimeDeploymentProcessor(
            context,
            runtime,
            new UnusedReceiptVerifier());

        Assert.True(await processor.TryProcessNextAsync(
            "deployment-worker",
            CancellationToken.None));

        context.ChangeTracker.Clear();
        var deployment = await context.Deployments.SingleAsync();
        Assert.Equal(seeded.DeploymentId, deployment.Id);
        Assert.Equal("pending", deployment.Status);
        Assert.Equal("runtime_bundle_lineage_mismatch", deployment.FailureCode);
        Assert.Null(deployment.DeployedBundleHash);
        Assert.Null(deployment.TestSuiteHash);
    }

    [PostgresFact]
    public async Task Staging_ready_state_is_tied_to_exact_bundle_and_suite_hashes()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var seeded = await SeedPendingDeploymentAsync(database.Services);
        var suiteHash = new string('d', 64);
        var compiledBundleHash = new string('b', 64);
        var runtime = new StubRuntimeDeploymentClient(
            seeded.ArtifactHash,
            compiledBundleHash,
            suiteHash);
        await using var scope = database.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
        var processor = new RuntimeDeploymentProcessor(
            context,
            runtime,
            new UnusedReceiptVerifier());

        Assert.True(await processor.TryProcessNextAsync(
            "deployment-worker",
            CancellationToken.None));

        context.ChangeTracker.Clear();
        var deployment = await context.Deployments
            .Include(value => value.ErpBundleVersion)
            .SingleAsync();
        Assert.Equal("ready", deployment.Status);
        Assert.Equal("passed", deployment.TestsStatus);
        Assert.Equal(seeded.ArtifactHash, deployment.ErpBundleVersion.ContentHash);
        Assert.Equal(compiledBundleHash, deployment.DeployedBundleHash);
        Assert.Equal(suiteHash, deployment.TestSuiteHash);
    }

    private static async Task<SeededDeployment> SeedPendingDeploymentAsync(
        IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
        var customer = new CustomerAccount
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            UserName = $"{Guid.NewGuid():N}@example.test",
            Name = "Runtime lineage owner"
        };
        var tenant = new Tenant
        {
            StableId = Guid.NewGuid().ToString("N"),
            Slug = $"lineage-{Guid.NewGuid():N}"[..40],
            CompanyName = "Runtime lineage",
            Owner = customer,
            OwnerId = customer.Id
        };
        var environment = new TenantEnvironment
        {
            Tenant = tenant,
            TenantId = tenant.Id,
            Environment = "staging",
            Host = "lineage.staging.localhost",
            DatabaseName = TenantDatabaseNamePolicy.BuildExpected(
                tenant.StableId,
                "staging"),
            Status = "pending_deployment"
        };
        tenant.Environments.Add(environment);
        var project = new GenerationProject
        {
            Customer = customer,
            CustomerId = customer.Id,
            Tenant = tenant,
            TenantId = tenant.Id,
            CompanyName = "Runtime lineage",
            Goal = "Prove exact deployment lineage",
            Status = "generated"
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            GenerationProjectId = project.Id,
            Status = GenerationRunStatuses.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var bundleHash = new string('a', 64);
        var artifact = TestGenerationArtifactFactory.Create(
            run,
            """
            {
              "name":"Lineage ERP",
              "summary":"Test",
              "modules":[],
              "entities":[],
              "workflows":[],
              "reports":[],
              "dashboards":[],
              "permissions":[],
              "acceptanceTests":[],
              "assumptions":[],
              "openIssues":[]
            }
            """,
            bundleHash);
        var bundle = new ErpBundleVersion
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            GenerationArtifactVersion = artifact,
            GenerationArtifactVersionId = artifact.Id,
            BundleId = $"erp_{project.Id:N}",
            Version = 1,
            ContentHash = bundleHash,
        };
        var deployment = new Deployment
        {
            Tenant = tenant,
            TenantId = tenant.Id,
            ErpBundleVersion = bundle,
            ErpBundleVersionId = bundle.Id,
            Environment = "staging",
            Status = "pending",
            OperationId = Guid.NewGuid().ToString("N")
        };
        context.AddRange(customer, tenant, project, run, bundle, deployment);
        await context.SaveChangesAsync();
        return new SeededDeployment(deployment.Id, bundleHash);
    }

    private sealed record SeededDeployment(Guid DeploymentId, string ArtifactHash);

    private sealed class StubRuntimeDeploymentClient(
        string artifactHash,
        string bundleHash,
        string testSuiteHash) : IRuntimeDeploymentClient
    {
        public Task<CanonicalRuntimeDeploymentViewModel> DeployCanonicalAsync(
            CanonicalRuntimeDeploymentInputModel input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CanonicalRuntimeDeploymentViewModel(
                true,
                "ready",
                input.OperationId,
                input.Target,
                "https://lineage.staging.localhost",
                "bundle-lineage",
                "1",
                artifactHash,
                bundleHash,
                "deployment-lineage",
                "passed",
                testSuiteHash));

        public Task<RuntimeOwnerActivationViewModel> CreateActivationAsync(
            RuntimeOwnerActivationInputModel input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeProductionPromotionViewModel> PromoteProductionAsync(
            RuntimeProductionPromotionInputModel input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedReceiptVerifier
        : IRuntimePromotionReceiptVerifier
    {
        public bool IsConfigured => false;
        public bool Verify(string? payloadHash, string? signature) => false;
    }
}
