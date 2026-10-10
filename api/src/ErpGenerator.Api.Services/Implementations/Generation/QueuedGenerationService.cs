using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class QueuedGenerationService(
    ErpGeneratorDbContext dbContext,
    IGenerationEvidenceService evidence,
    IGenerationResumeService resumeService) : IQueuedGenerationService
{
    public async Task<bool> TryResumeNextReadyAsync(
        CancellationToken cancellationToken)
    {
        var runId = await dbContext.GenerationRuns
            .AsNoTracking()
            .Where(value =>
                value.Status == GenerationRunStatuses.WaitingForInput &&
                value.LeaseId == null &&
                !value.PendingWorkItems.Any(item =>
                    item.Blocking &&
                    item.Status == "pending"))
            .OrderBy(value => value.UpdatedAt)
            .Select(value => (Guid?)value.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return runId.HasValue &&
               await resumeService.TryResumeReadyAsync(
                   runId.Value,
                   "worker_reconciliation",
                   cancellationToken);
    }

    public async Task<bool> TryStartNextReadyAsync(CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
                transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var project = await dbContext.GenerationProjects
                .Where(value => value.GenerationRequestedAt != null)
                .Where(value => !value.GenerationRuns.Any(run =>
                    run.Status == "queued" ||
                    run.Status == "running" ||
                    run.Status == "waiting_for_input"))
                .Where(value => !value.InputSources.Any(source =>
                    source.RetainContentUntil != null &&
                    source.RetainContentUntil <= now))
                .Where(value => !value.InputSources.Any(source =>
                    source.ProviderConfigKey == "notion" &&
                    source.ScopeMode == "unconfigured"))
                .Where(value => !value.InputSources.Any(source =>
                    source.Status == "draft" ||
                    source.Status == "connecting" ||
                    source.Status == "authenticated" ||
                    source.Status == "connected" ||
                    source.Status == "awaiting_scope" ||
                    source.Status == "awaiting_selection" ||
                    source.Status == "syncing" ||
                    source.Status == "sync_failed" ||
                    source.Status == "requires_reconnect" ||
                    source.Status == "connection_failed" ||
                    source.Status == "error" ||
                    source.Status == "expired"))
                .OrderBy(value => value.GenerationRequestedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (project is null)
            {
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return false;
            }

            var active = new GenerationRun
            {
                GenerationProjectId = project.Id,
                RequestedMode = "default"
            };
            GenerationRunEventFactory.Add(
                dbContext,
                active,
                GenerationRunEventTypes.Queued,
                "Generation was queued after connected evidence became ready.");
            project.GenerationRuns.Add(active);
            dbContext.GenerationRuns.Add(active);

            project.GenerationRequestedAt = null;
            project.Status = "generating";
            await dbContext.SaveChangesAsync(cancellationToken);
            await evidence.CaptureAsync(active.Id, refreshConnectedSources: false, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }
}
