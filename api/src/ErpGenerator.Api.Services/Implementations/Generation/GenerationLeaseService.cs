using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class GenerationLeaseService(
    ErpGeneratorDbContext dbContext,
    IOptions<GenerationOrchestrationOptions> options) : IGenerationLeaseService
{
    public async Task<int> RecoverExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var finalizedCancellations = await FinalizeAbandonedCancellationsAsync(
            now,
            cancellationToken);
        if (!dbContext.Database.IsRelational())
        {
            var trackedExpired = await dbContext.GenerationRuns
                .Include(value => value.Events)
                .Include(value => value.AgentWorkItems)
                    .ThenInclude(value => value.Attempts)
                .Where(value =>
                    value.Status == GenerationRunStatuses.Running &&
                    value.LeaseExpiresAt < now)
                .ToArrayAsync(cancellationToken);
            foreach (var run in trackedExpired)
            {
                RecoverAgentWorkItems(run, now);
                run.Status = GenerationRunStatuses.Queued;
                run.NextAttemptAt = now;
                run.LeaseId = null;
                run.LeaseOwner = null;
                run.LeaseExpiresAt = null;
                GenerationRunEventFactory.Add(
                    dbContext,
                    run,
                    GenerationRunEventTypes.LeaseRecovered,
                    "An expired worker lease was recovered and the run was queued again.");
            }
            if (trackedExpired.Length > 0)
                await dbContext.SaveChangesAsync(cancellationToken);
            return finalizedCancellations + trackedExpired.Length;
        }

        var expiredRuns = await dbContext.GenerationRuns
            .Where(value =>
                value.Status == GenerationRunStatuses.Running &&
                value.LeaseExpiresAt < now)
            .Select(value => value.Id)
            .ToArrayAsync(cancellationToken);
        if (expiredRuns.Length == 0) return finalizedCancellations;

        var recovered = await dbContext.GenerationRuns
            .Where(value =>
                expiredRuns.Contains(value.Id) &&
                value.Status == GenerationRunStatuses.Running &&
                value.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, GenerationRunStatuses.Queued)
                .SetProperty(value => value.NextAttemptAt, now)
                .SetProperty(value => value.LeaseId, (Guid?)null)
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);
        if (recovered == 0) return finalizedCancellations;

        await dbContext.GenerationAgentExecutionAttempts
            .Where(value =>
                expiredRuns.Contains(
                    value.GenerationAgentWorkItem.GenerationRunId) &&
                value.Status == AgentAttemptStatuses.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, AgentAttemptStatuses.Abandoned)
                .SetProperty(value => value.CompletedAt, now)
                .SetProperty(value => value.FailureCode, "stale_lease")
                .SetProperty(
                    value => value.FailureMessage,
                    "The worker lease expired and the work item was reclaimed."),
                cancellationToken);
        await dbContext.GenerationAgentWorkItems
            .Where(value =>
                expiredRuns.Contains(value.GenerationRunId) &&
                value.Status == AgentWorkItemStatuses.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, AgentWorkItemStatuses.Queued)
                .SetProperty(value => value.NotBefore, now)
                .SetProperty(value => value.LeaseId, (Guid?)null)
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(value => value.FailureCode, "stale_lease")
                .SetProperty(
                    value => value.FailureMessage,
                    "The worker lease expired and the work item was reclaimed."),
                cancellationToken);
        var recoveredRuns = await dbContext.GenerationRuns
            .Include(value => value.GenerationProject)
            .Include(value => value.Events)
            .Where(value =>
                expiredRuns.Contains(value.Id) &&
                value.Status == GenerationRunStatuses.Queued &&
                value.NextAttemptAt == now &&
                value.LeaseId == null)
            .ToArrayAsync(cancellationToken);
        foreach (var run in recoveredRuns)
        {
            GenerationRunEventFactory.Add(
                dbContext,
                run,
                GenerationRunEventTypes.LeaseRecovered,
                "An expired worker lease was recovered and the run was queued again.");
            GenerationRunEventFactory.Add(
                dbContext,
                run,
                GenerationRunEventTypes.AgentWorkItemLeaseRecovered,
                "Expired agent work-item leases were reclaimed with the generation run.");
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return finalizedCancellations + recovered;
    }

    public async Task<GenerationLeaseClaim?> ClaimNextAsync(
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            var trackedRun = await dbContext.GenerationRuns
                .Where(value =>
                    value.Status == GenerationRunStatuses.Queued &&
                    (value.NextAttemptAt == null || value.NextAttemptAt <= now))
                .OrderBy(value => value.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (trackedRun is null) return null;
            var trackedLeaseId = Guid.NewGuid();
            trackedRun.Status = GenerationRunStatuses.Running;
            trackedRun.NextAttemptAt = null;
            trackedRun.LeaseId = trackedLeaseId;
            trackedRun.LeaseOwner = workerId;
            trackedRun.LeaseExpiresAt = now.Add(LeaseDuration());
            trackedRun.HeartbeatAt = now;
            trackedRun.StartedAt ??= now;
            trackedRun.ActualMode = "simple";
            trackedRun.ClaimCount += 1;
            await dbContext.SaveChangesAsync(cancellationToken);
            return new GenerationLeaseClaim(trackedRun.Id, trackedLeaseId);
        }

        var runId = await dbContext.GenerationRuns.AsNoTracking()
            .Where(value =>
                value.Status == GenerationRunStatuses.Queued &&
                (value.NextAttemptAt == null || value.NextAttemptAt <= now))
            .OrderBy(value => value.CreatedAt)
            .Select(value => (Guid?)value.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (runId is null) return null;

        var leaseId = Guid.NewGuid();
        var claimed = await dbContext.GenerationRuns
            .Where(value =>
                value.Id == runId &&
                value.Status == GenerationRunStatuses.Queued &&
                (value.NextAttemptAt == null || value.NextAttemptAt <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, GenerationRunStatuses.Running)
                .SetProperty(value => value.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(value => value.LeaseId, leaseId)
                .SetProperty(value => value.LeaseOwner, workerId)
                .SetProperty(value => value.LeaseExpiresAt, now.Add(LeaseDuration()))
                .SetProperty(value => value.HeartbeatAt, now)
                .SetProperty(value => value.StartedAt, value => value.StartedAt ?? now)
                .SetProperty(value => value.ActualMode, "simple")
                .SetProperty(value => value.ClaimCount, value => value.ClaimCount + 1),
                cancellationToken);
        return claimed == 0 ? null : new GenerationLeaseClaim(runId.Value, leaseId);
    }

    public async Task<bool> RenewAsync(
        Guid runId,
        Guid leaseId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            var trackedRun = await dbContext.GenerationRuns.SingleOrDefaultAsync(
                value =>
                    value.Id == runId &&
                    value.Status == GenerationRunStatuses.Running &&
                    value.LeaseId == leaseId,
                cancellationToken);
            if (trackedRun is null) return false;
            trackedRun.HeartbeatAt = now;
            trackedRun.LeaseExpiresAt = now.Add(LeaseDuration());
            var trackedItems = await dbContext.GenerationAgentWorkItems
                .Where(value =>
                    value.GenerationRunId == runId &&
                    value.Status == AgentWorkItemStatuses.Running &&
                    value.LeaseId == leaseId)
                .ToArrayAsync(cancellationToken);
            foreach (var workItem in trackedItems)
            {
                workItem.HeartbeatAt = now;
                workItem.LeaseExpiresAt = trackedRun.LeaseExpiresAt;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        var renewed = await dbContext.GenerationRuns
            .Where(value =>
                value.Id == runId &&
                value.Status == GenerationRunStatuses.Running &&
                value.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.HeartbeatAt, now)
                .SetProperty(value => value.LeaseExpiresAt, now.Add(LeaseDuration())),
                cancellationToken) == 1;
        if (!renewed) return false;
        await dbContext.GenerationAgentWorkItems
            .Where(value =>
                value.GenerationRunId == runId &&
                value.Status == AgentWorkItemStatuses.Running &&
                value.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.HeartbeatAt, now)
                .SetProperty(value => value.LeaseExpiresAt, now.Add(LeaseDuration())),
                cancellationToken);
        return true;
    }

    private TimeSpan LeaseDuration() =>
        TimeSpan.FromSeconds(Math.Clamp(
            options.Value.LeaseDurationSeconds,
            30,
            86_400));

    private async Task<int> FinalizeAbandonedCancellationsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            var trackedRuns = await dbContext.GenerationRuns
                .Include(value => value.GenerationProject)
                .Include(value => value.Stages)
                .Include(value => value.AgentWorkItems)
                    .ThenInclude(value => value.Attempts)
                .Include(value => value.Events)
                .Where(value =>
                    value.Status == GenerationRunStatuses.CancellationRequested &&
                    (value.LeaseId == null || value.LeaseExpiresAt < now))
                .ToArrayAsync(cancellationToken);
            foreach (var run in trackedRuns)
            {
                FinalizeCancellation(run, now);
            }
            if (trackedRuns.Length > 0)
                await dbContext.SaveChangesAsync(cancellationToken);
            return trackedRuns.Length;
        }

        var runIds = await dbContext.GenerationRuns
            .Where(value =>
                value.Status == GenerationRunStatuses.CancellationRequested &&
                (value.LeaseId == null || value.LeaseExpiresAt < now))
            .Select(value => value.Id)
            .ToArrayAsync(cancellationToken);
        if (runIds.Length == 0) return 0;

        var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
            : null;
        try
        {
            var finalized = await dbContext.GenerationRuns
                .Where(value =>
                    runIds.Contains(value.Id) &&
                    value.Status == GenerationRunStatuses.CancellationRequested &&
                    (value.LeaseId == null || value.LeaseExpiresAt < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.Status, GenerationRunStatuses.Cancelled)
                    .SetProperty(value => value.CancelledAt, now)
                    .SetProperty(value => value.CompletedAt, now)
                    .SetProperty(value => value.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(value => value.HeartbeatAt, now)
                    .SetProperty(value => value.LeaseId, (Guid?)null)
                    .SetProperty(value => value.LeaseOwner, (string?)null)
                    .SetProperty(value => value.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken);
            if (finalized == 0)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            var runs = await dbContext.GenerationRuns
                .Include(value => value.GenerationProject)
                .Include(value => value.Stages)
                .Include(value => value.AgentWorkItems)
                    .ThenInclude(value => value.Attempts)
                .Include(value => value.Events)
                .Where(value =>
                    runIds.Contains(value.Id) &&
                    value.Status == GenerationRunStatuses.Cancelled &&
                    value.CancelledAt == now)
                .ToArrayAsync(cancellationToken);
            foreach (var run in runs)
            {
                run.GenerationProject.Status = "cancelled";
                CancelAgentWorkItems(run, now);
                foreach (var stage in run.Stages.Where(value =>
                             value.Status == GenerationStageStatuses.Running))
                {
                    stage.Status = GenerationStageStatuses.Cancelled;
                    stage.CompletedAt = now;
                    stage.FailureCode = null;
                    stage.FailureMessage = null;
                }
                GenerationRunEventFactory.Add(
                    dbContext,
                    run,
                    GenerationRunEventTypes.Cancelled,
                    "Generation was cancelled after its worker stopped.");
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return finalized;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private void FinalizeCancellation(
        GenerationRun run,
        DateTimeOffset now)
    {
        GenerationRunTransitionPolicy.EnsureAllowed(
            run.Status,
            GenerationRunStatuses.Cancelled);
        run.Status = GenerationRunStatuses.Cancelled;
        run.CancelledAt = now;
        run.CompletedAt = now;
        run.NextAttemptAt = null;
        run.HeartbeatAt = now;
        run.LeaseId = null;
        run.LeaseOwner = null;
        run.LeaseExpiresAt = null;
        run.GenerationProject.Status = "cancelled";
        CancelAgentWorkItems(run, now);
        foreach (var stage in run.Stages.Where(value =>
                     value.Status == GenerationStageStatuses.Running))
        {
            stage.Status = GenerationStageStatuses.Cancelled;
            stage.CompletedAt = now;
            stage.FailureCode = null;
            stage.FailureMessage = null;
        }
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.Cancelled,
            "Generation was cancelled after its worker stopped.");
    }

    private static void RecoverAgentWorkItems(
        GenerationRun run,
        DateTimeOffset now)
    {
        foreach (var workItem in run.AgentWorkItems.Where(value =>
                     value.Status == AgentWorkItemStatuses.Running))
        {
            workItem.Status = AgentWorkItemStatuses.Queued;
            workItem.NotBefore = now;
            workItem.LeaseId = null;
            workItem.LeaseOwner = null;
            workItem.LeaseExpiresAt = null;
            workItem.FailureCode = "stale_lease";
            workItem.FailureMessage =
                "The worker lease expired and the work item was reclaimed.";
            foreach (var attempt in workItem.Attempts.Where(value =>
                         value.Status == AgentAttemptStatuses.Running))
            {
                attempt.Status = AgentAttemptStatuses.Abandoned;
                attempt.CompletedAt = now;
                attempt.FailureCode = "stale_lease";
                attempt.FailureMessage = workItem.FailureMessage;
            }
        }
    }

    internal static void CancelAgentWorkItems(
        GenerationRun run,
        DateTimeOffset now)
    {
        foreach (var workItem in run.AgentWorkItems.Where(value =>
                     value.Status is AgentWorkItemStatuses.Queued or
                         AgentWorkItemStatuses.Running or
                         AgentWorkItemStatuses.WaitingForInput))
        {
            workItem.Status = AgentWorkItemStatuses.Cancelled;
            workItem.CompletedAt = now;
            workItem.LeaseId = null;
            workItem.LeaseOwner = null;
            workItem.LeaseExpiresAt = null;
            foreach (var attempt in workItem.Attempts.Where(value =>
                         value.Status == AgentAttemptStatuses.Running))
            {
                attempt.Status = AgentAttemptStatuses.Cancelled;
                attempt.CompletedAt = now;
            }
        }
    }
}
