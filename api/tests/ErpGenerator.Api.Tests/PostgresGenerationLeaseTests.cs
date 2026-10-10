using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Api.Services.Implementations.Admin;

namespace ErpGenerator.Api.Tests;

public sealed class PostgresGenerationLeaseTests
{
    [PostgresFact]
    public async Task Concurrent_duplicate_dispatch_has_exactly_one_lease_winner()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var runId = await database.SeedQueuedRunAsync();
        var now = DateTimeOffset.UtcNow;

        var first = ClaimAsync(database.Services, "worker-a", now);
        var second = ClaimAsync(database.Services, "worker-b", now);
        var claims = await Task.WhenAll(first, second);

        var winner = Assert.Single(claims, value => value is not null);
        Assert.Equal(runId, winner!.RunId);
        await using var scope = database.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
        var persisted = await context.GenerationRuns.SingleAsync();
        Assert.Equal(GenerationRunStatuses.Running, persisted.Status);
        Assert.Equal(1, persisted.ClaimCount);
        Assert.Equal(winner.LeaseId, persisted.LeaseId);
    }

    [PostgresFact]
    public async Task Expired_lease_is_reclaimed_once_and_stale_worker_is_fenced()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var runId = await database.SeedQueuedRunAsync();
        var first = await ClaimAsync(
            database.Services,
            "worker-a",
            DateTimeOffset.UtcNow);
        Assert.NotNull(first);

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            await context.GenerationRuns
                .Where(value => value.Id == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    value => value.LeaseExpiresAt,
                    DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var recoveredAt = DateTimeOffset.UtcNow;
        await using (var scope = database.Services.CreateAsyncScope())
        {
            var leaseService = scope.ServiceProvider
                .GetRequiredService<IGenerationLeaseService>();
            Assert.Equal(
                1,
                await leaseService.RecoverExpiredAsync(
                    recoveredAt,
                    CancellationToken.None));
        }
        var second = await ClaimAsync(
            database.Services,
            "worker-b",
            recoveredAt.AddMilliseconds(1));
        Assert.NotNull(second);

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var leaseService = scope.ServiceProvider
                .GetRequiredService<IGenerationLeaseService>();
            Assert.False(await leaseService.RenewAsync(
                runId,
                first!.LeaseId,
                recoveredAt,
                CancellationToken.None));
            Assert.True(await leaseService.RenewAsync(
                runId,
                second!.LeaseId,
                recoveredAt,
                CancellationToken.None));
        }

        await using var verification = database.Services.CreateAsyncScope();
        var dbContext = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await dbContext.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync();
        Assert.Equal(2, run.ClaimCount);
        Assert.Single(
            run.Events,
            value => value.EventType == GenerationRunEventTypes.LeaseRecovered);
    }

    [PostgresFact]
    public async Task Replaced_lease_blocks_stale_tracked_run_from_completing()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var runId = await database.SeedQueuedRunAsync();
        var first = await ClaimAsync(
            database.Services,
            "worker-a",
            DateTimeOffset.UtcNow);
        Assert.NotNull(first);

        await using var staleScope = database.Services.CreateAsyncScope();
        var staleContext = staleScope.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var staleRun = await staleContext.GenerationRuns.SingleAsync(
            value => value.Id == runId);

        await using (var replacementScope = database.Services.CreateAsyncScope())
        {
            var replacementContext = replacementScope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            await replacementContext.GenerationRuns
                .Where(value => value.Id == runId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.LeaseId, Guid.NewGuid())
                    .SetProperty(value => value.LeaseOwner, "worker-b"));
        }

        staleRun.Status = GenerationRunStatuses.Completed;
        staleRun.CompletedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            staleContext.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task Duplicate_artifact_hash_for_one_run_is_rejected()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var runId = await database.SeedQueuedRunAsync();
        await using var scope = database.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
        var run = await context.GenerationRuns
            .Include(value => value.GenerationProject)
            .SingleAsync(value => value.Id == runId);
        var bundleId = $"erp_{run.GenerationProjectId:N}";
        var contentHash = new string('b', 64);
        var artifact = TestGenerationArtifactFactory.Create(
            run,
            """{"name":"one"}""",
            contentHash);
        context.ErpBundleVersions.AddRange(
            new ErpBundleVersion
            {
                GenerationRunId = runId,
                GenerationArtifactVersion = artifact,
                GenerationArtifactVersionId = artifact.Id,
                BundleId = bundleId,
                Version = 1,
                ContentHash = contentHash
            },
            new ErpBundleVersion
            {
                GenerationRunId = runId,
                GenerationArtifactVersion = artifact,
                GenerationArtifactVersionId = artifact.Id,
                BundleId = bundleId,
                Version = 2,
                ContentHash = contentHash
            });

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task Concurrent_cancellation_finalizers_create_one_terminal_event()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var runId = await database.SeedQueuedRunAsync();
        Assert.NotNull(await ClaimAsync(
            database.Services,
            "worker-a",
            DateTimeOffset.UtcNow));

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            var administration = new AdminOrchestrationService(context);
            await administration.CancelAsync(
                Guid.NewGuid(),
                "orchestration-admin@example.test",
                runId,
                "Prove cancellation finalization is safe across replicas.",
                CancellationToken.None);
        }

        var now = DateTimeOffset.UtcNow;
        var finalized = await Task.WhenAll(
            RecoverAsync(database.Services, now),
            RecoverAsync(database.Services, now));
        Assert.Equal(1, finalized.Sum());

        await using var verification = database.Services.CreateAsyncScope();
        var dbContext = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await dbContext.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync(value => value.Id == runId);
        Assert.Equal(GenerationRunStatuses.Cancelled, run.Status);
        Assert.Single(
            run.Events,
            value => value.EventType == GenerationRunEventTypes.Cancelled);
    }

    private static async Task<GenerationLeaseClaim?> ClaimAsync(
        IServiceProvider services,
        string workerId,
        DateTimeOffset now)
    {
        await using var scope = services.CreateAsyncScope();
        var leases = scope.ServiceProvider
            .GetRequiredService<IGenerationLeaseService>();
        return await leases.ClaimNextAsync(
            workerId,
            now,
            CancellationToken.None);
    }

    private static async Task<int> RecoverAsync(
        IServiceProvider services,
        DateTimeOffset now)
    {
        await using var scope = services.CreateAsyncScope();
        var leases = scope.ServiceProvider
            .GetRequiredService<IGenerationLeaseService>();
        return await leases.RecoverExpiredAsync(
            now,
            CancellationToken.None);
    }
}
