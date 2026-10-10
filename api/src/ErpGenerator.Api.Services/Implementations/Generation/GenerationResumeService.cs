using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class GenerationResumeService(
    ErpGeneratorDbContext dbContext,
    IGenerationEvidenceService evidence,
    TimeProvider timeProvider,
    ILogger<GenerationResumeService> logger) : IGenerationResumeService
{
    public async Task<bool> TryResumeReadyAsync(
        Guid generationRunId,
        string trigger,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            }

            var now = timeProvider.GetUtcNow();
            if (!await TryClaimReadyRunAsync(
                    generationRunId,
                    now,
                    cancellationToken))
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                return false;
            }

            dbContext.ChangeTracker.Clear();
            await evidence.CaptureAsync(
                generationRunId,
                refreshConnectedSources: false,
                cancellationToken);
            dbContext.ChangeTracker.Clear();

            var run = await dbContext.GenerationRuns
                .Include(value => value.GenerationProject)
                .SingleAsync(
                    value => value.Id == generationRunId,
                    cancellationToken);
            run.GenerationProject.Status = "generating";
            GenerationRunEventFactory.Add(
                dbContext,
                run,
                GenerationRunEventTypes.InputReceived,
                "All blocking information was received and generation resumed.",
                safeMetadataJson: JsonSerializer.Serialize(new
                {
                    Trigger = SafeTrigger(trigger)
                }));
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Generation run {GenerationRunId} resumed after all blocking input was resolved. Trigger: {Trigger}.",
                generationRunId,
                SafeTrigger(trigger));
            return true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(
                        CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    logger.LogError(
                        rollbackException,
                        "Generation resume transaction rollback failed for run {GenerationRunId}.",
                        generationRunId);
                }
                await transaction.DisposeAsync();
                transaction = null;
            }

            await RecordFailureAsync(
                generationRunId,
                exception,
                CancellationToken.None);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<bool> TryClaimReadyRunAsync(
        Guid generationRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            var transitioned = await dbContext.GenerationRuns
                .Where(value =>
                    value.Id == generationRunId &&
                    value.Status == GenerationRunStatuses.WaitingForInput &&
                    value.LeaseId == null &&
                    !value.PendingWorkItems.Any(item =>
                        item.Blocking &&
                        item.Status == "pending"))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        value => value.Status,
                        GenerationRunStatuses.Queued)
                    .SetProperty(value => value.LeaseId, (Guid?)null)
                    .SetProperty(value => value.LeaseOwner, (string?)null)
                    .SetProperty(
                        value => value.LeaseExpiresAt,
                        (DateTimeOffset?)null)
                    .SetProperty(value => value.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(value => value.FailureDisposition, (string?)null)
                    .SetProperty(value => value.FailureCode, (string?)null)
                    .SetProperty(value => value.FailureMessage, (string?)null)
                    .SetProperty(
                        value => value.ResumeAttemptCount,
                        value => value.ResumeAttemptCount + 1)
                    .SetProperty(value => value.ResumeFailureCount, 0)
                    .SetProperty(value => value.ResumeLastAttemptAt, now)
                    .SetProperty(
                        value => value.ResumeFailureMessage,
                        (string?)null)
                    .SetProperty(value => value.UpdatedAt, now),
                    cancellationToken);
            return transitioned == 1;
        }

        dbContext.ChangeTracker.Clear();
        var run = await dbContext.GenerationRuns
            .Include(value => value.GenerationProject)
            .Include(value => value.PendingWorkItems)
            .SingleOrDefaultAsync(
                value => value.Id == generationRunId,
                cancellationToken);
        if (run is null ||
            run.Status != GenerationRunStatuses.WaitingForInput ||
            run.LeaseId is not null ||
            run.PendingWorkItems.Any(value =>
                value.Blocking &&
                value.Status == "pending"))
        {
            return false;
        }

        GenerationRunTransitionPolicy.EnsureAllowed(
            run.Status,
            GenerationRunStatuses.Queued);
        run.Status = GenerationRunStatuses.Queued;
        run.LeaseId = null;
        run.LeaseOwner = null;
        run.LeaseExpiresAt = null;
        run.NextAttemptAt = null;
        run.FailureDisposition = null;
        run.FailureCode = null;
        run.FailureMessage = null;
        run.ResumeAttemptCount += 1;
        run.ResumeFailureCount = 0;
        run.ResumeLastAttemptAt = now;
        run.ResumeFailureMessage = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task RecordFailureAsync(
        Guid generationRunId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var now = timeProvider.GetUtcNow();
        var message =
            "Automatic generation resume did not complete. The worker will retry.";
        try
        {
            if (dbContext.Database.IsRelational())
            {
                await dbContext.GenerationRuns
                    .Where(value =>
                        value.Id == generationRunId &&
                        value.Status ==
                        GenerationRunStatuses.WaitingForInput &&
                        !value.PendingWorkItems.Any(item =>
                            item.Blocking &&
                            item.Status == "pending"))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(
                            value => value.ResumeAttemptCount,
                            value => value.ResumeAttemptCount + 1)
                        .SetProperty(
                            value => value.ResumeFailureCount,
                            value => value.ResumeFailureCount + 1)
                        .SetProperty(
                            value => value.ResumeLastAttemptAt,
                            now)
                        .SetProperty(
                            value => value.ResumeFailureMessage,
                            message)
                        .SetProperty(value => value.UpdatedAt, now),
                        cancellationToken);
            }
            else
            {
                var run = await dbContext.GenerationRuns
                    .Include(value => value.PendingWorkItems)
                    .SingleOrDefaultAsync(
                        value => value.Id == generationRunId,
                        cancellationToken);
                if (run is not null &&
                    run.Status ==
                    GenerationRunStatuses.WaitingForInput &&
                    !run.PendingWorkItems.Any(value =>
                        value.Blocking &&
                        value.Status == "pending"))
                {
                    run.ResumeAttemptCount += 1;
                    run.ResumeFailureCount += 1;
                    run.ResumeLastAttemptAt = now;
                    run.ResumeFailureMessage = message;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }
        catch (Exception persistenceException)
        {
            logger.LogError(
                persistenceException,
                "Generation resume failure state could not be persisted for run {GenerationRunId}.",
                generationRunId);
        }

        logger.LogError(
            exception,
            "Generation run {GenerationRunId} could not resume. The existing generation worker will retry.",
            generationRunId);
    }

    private static string SafeTrigger(string value)
    {
        var trigger = value?.Trim() ?? string.Empty;
        if (trigger.Length == 0) return "unspecified";
        return trigger.Length <= 80 ? trigger : trigger[..80];
    }
}
