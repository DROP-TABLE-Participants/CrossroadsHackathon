using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Tests;

public sealed class GeneratedStagingDeploymentQueueTests
{
    [Fact]
    public async Task Completed_generated_bundle_is_queued_for_customer_staging_once()
    {
        await using var dbContext = CreateContext();
        var customer = new CustomerAccount
        {
            Email = "owner@example.test",
            NormalizedEmail = "OWNER@EXAMPLE.TEST",
            UserName = "owner@example.test",
            NormalizedUserName = "OWNER@EXAMPLE.TEST",
            Name = "Owner",
            PasswordHash = "unused"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CompanyName = "Dandelion Chocolate",
            Goal = "Replace paper processes",
            Status = "generated",
            DeployToCustomerStaging = true
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            Status = "completed",
            CompletedAt = DateTimeOffset.UtcNow
        };
        var artifact = TestGenerationArtifactFactory.Create(
            run,
            "{}",
            new string('a', 64));
        var bundle = new ErpBundleVersion
        {
            GenerationRun = run,
            GenerationArtifactVersion = artifact,
            GenerationArtifactVersionId = artifact.Id,
            BundleId = $"erp_{project.Id:N}",
            Version = 1,
            ContentHash = new string('a', 64),
            Summary = "Generated ERP"
        };
        run.BundleVersions.Add(bundle);
        project.GenerationRuns.Add(run);
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();

        var queue = new GeneratedStagingDeploymentQueue(
            dbContext,
            Options.Create(new RuntimeAddressOptions
            {
                Scheme = "http",
                StagingPublicHostSuffix = "staging.localhost:5057"
            }));

        Assert.True(await queue.TryQueueNextAsync(CancellationToken.None));
        Assert.False(await queue.TryQueueNextAsync(CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var savedProject = await dbContext.GenerationProjects
            .Include(value => value.Tenant)
                .ThenInclude(value => value!.Environments)
            .SingleAsync();
        var tenant = Assert.IsType<Tenant>(savedProject.Tenant);
        Assert.Equal("dandelion-chocolate", tenant.Slug);
        var staging = Assert.Single(tenant.Environments);
        Assert.Equal("staging", staging.Environment);
        Assert.Equal("dandelion-chocolate.staging.localhost", staging.Host);
        Assert.Equal("http://dandelion-chocolate.staging.localhost:5057", staging.RuntimeUrl);
        Assert.Equal(TenantDatabaseNamePolicy.BuildExpected(tenant.StableId, "staging"), staging.DatabaseName);
        Assert.Equal("pending_deployment", staging.Status);

        var deployment = Assert.Single(await dbContext.Deployments.ToListAsync());
        Assert.Equal("staging", deployment.Environment);
        Assert.Equal("pending", deployment.Status);
        Assert.Equal(bundle.Id, deployment.ErpBundleVersionId);
        Assert.Equal(tenant.Id, deployment.TenantId);
    }

    [Fact]
    public async Task Completed_bundle_is_not_staged_when_customer_disabled_the_target()
    {
        await using var dbContext = CreateContext();
        var customer = new CustomerAccount
        {
            Email = "owner@example.test",
            NormalizedEmail = "OWNER@EXAMPLE.TEST",
            UserName = "owner@example.test",
            NormalizedUserName = "OWNER@EXAMPLE.TEST",
            Name = "Owner",
            PasswordHash = "unused"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CompanyName = "Dandelion Chocolate",
            Goal = "Replace paper processes",
            Status = "generated",
            DeployToCustomerStaging = false
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            Status = "completed"
        };
        var artifact = TestGenerationArtifactFactory.Create(
            run,
            "{}",
            new string('b', 64));
        run.BundleVersions.Add(new ErpBundleVersion
        {
            GenerationRun = run,
            GenerationArtifactVersion = artifact,
            GenerationArtifactVersionId = artifact.Id,
            BundleId = $"erp_{project.Id:N}",
            Version = 1,
            ContentHash = new string('b', 64),
            Summary = "Generated ERP"
        });
        project.GenerationRuns.Add(run);
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();

        var queue = new GeneratedStagingDeploymentQueue(
            dbContext,
            Options.Create(new RuntimeAddressOptions()));

        Assert.False(await queue.TryQueueNextAsync(CancellationToken.None));
        Assert.Empty(await dbContext.Deployments.ToListAsync());
        Assert.Empty(await dbContext.Tenants.ToListAsync());
    }

    private static ErpGeneratorDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ErpGeneratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
