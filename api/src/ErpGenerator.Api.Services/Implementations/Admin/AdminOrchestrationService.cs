using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpGenerator.Api.Services.Implementations.Admin;

public sealed class AdminOrchestrationService(
    ErpGeneratorDbContext controlPlane,
    TimeProvider? timeProvider = null)
    : IAdminOrchestrationService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<AdminOrchestrationOverviewViewModel> GetOverviewAsync(
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var last24Hours = now.AddHours(-24);
        var runs = controlPlane.GenerationRuns.AsNoTracking();
        var queuedRunCount = await runs.CountAsync(
            value =>
                value.Status == GenerationRunStatuses.Queued &&
                (value.NextAttemptAt == null || value.NextAttemptAt <= now),
            cancellationToken);
        var runningRunCount = await runs.CountAsync(
            value => value.Status == GenerationRunStatuses.Running,
            cancellationToken);
        var resumingRunCount = await runs.CountAsync(
            value =>
                value.Status == GenerationRunStatuses.WaitingForInput &&
                !value.PendingWorkItems.Any(item =>
                    item.Blocking &&
                    item.Status == "pending") &&
                value.ResumeFailureCount <
                GenerationRunPresentationStatus.ResumeFailureAttentionThreshold,
            cancellationToken);
        var resumeFailedRunCount = await runs.CountAsync(
            value =>
                value.Status == GenerationRunStatuses.WaitingForInput &&
                !value.PendingWorkItems.Any(item =>
                    item.Blocking &&
                    item.Status == "pending") &&
                value.ResumeFailureCount >=
                GenerationRunPresentationStatus.ResumeFailureAttentionThreshold,
            cancellationToken);
        var waitingForInputRunCount = await runs.CountAsync(
            value =>
                value.Status == GenerationRunStatuses.WaitingForInput &&
                value.PendingWorkItems.Any(item =>
                    item.Blocking &&
                    item.Status == "pending"),
            cancellationToken);
        var cancellationRequestedRunCount = await runs.CountAsync(
            value => value.Status == GenerationRunStatuses.CancellationRequested,
            cancellationToken);
        var retryScheduledRunCount = await runs.CountAsync(
            value =>
                value.Status == GenerationRunStatuses.Queued &&
                value.NextAttemptAt > now,
            cancellationToken);
        var failedLast24HoursCount = await runs.CountAsync(
            value =>
                value.Status == GenerationRunStatuses.Failed &&
                value.CompletedAt >= last24Hours,
            cancellationToken);
        var staleLeaseCount = await runs.CountAsync(
            value =>
                value.Status == GenerationRunStatuses.Running &&
                value.LeaseExpiresAt < now,
            cancellationToken);
        var oldestQueuedAt = await runs
            .Where(value =>
                value.Status == GenerationRunStatuses.Queued &&
                (value.NextAttemptAt == null || value.NextAttemptAt <= now))
            .Select(value => (DateTimeOffset?)value.CreatedAt)
            .MinAsync(cancellationToken);
        var recent = await controlPlane.GenerationRuns
            .AsNoTracking()
            .AsSplitQuery()
            .Include(value => value.GenerationProject)
                .ThenInclude(value => value.Customer)
            .Include(value => value.Stages)
            .Include(value => value.AgentWorkItems)
                .ThenInclude(value => value.Attempts)
            .Include(value => value.PendingWorkItems)
            .OrderByDescending(value => value.UpdatedAt)
            .Take(12)
            .ToListAsync(cancellationToken);

        return new AdminOrchestrationOverviewViewModel(
            queuedRunCount,
            runningRunCount,
            resumingRunCount,
            resumeFailedRunCount,
            waitingForInputRunCount,
            cancellationRequestedRunCount,
            retryScheduledRunCount,
            failedLast24HoursCount,
            staleLeaseCount,
            oldestQueuedAt,
            recent.Select(MapListItem).ToArray());
    }

    public async Task<AdminGenerationRunListViewModel> ListAsync(
        string? search,
        string? status,
        string? stage,
        string? failureDisposition,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = AdminQueryRules.Page(page);
        pageSize = AdminQueryRules.PageSize(pageSize);
        var now = clock.GetUtcNow();
        var query = controlPlane.GenerationRuns.AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(value =>
                EF.Functions.ILike(value.GenerationProject.CompanyName, $"%{term}%") ||
                (value.GenerationProject.Customer.Email != null &&
                 EF.Functions.ILike(value.GenerationProject.Customer.Email, $"%{term}%")));
        }
        if (string.Equals(status, "retry_scheduled", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(value =>
                value.Status == GenerationRunStatuses.Queued &&
                value.NextAttemptAt > now);
        }
        else if (string.Equals(
                     status,
                     GenerationRunPresentationStatus.Resuming,
                     StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(value =>
                value.Status == GenerationRunStatuses.WaitingForInput &&
                !value.PendingWorkItems.Any(item =>
                    item.Blocking &&
                    item.Status == "pending") &&
                value.ResumeFailureCount <
                GenerationRunPresentationStatus.ResumeFailureAttentionThreshold);
        }
        else if (string.Equals(
                     status,
                     GenerationRunPresentationStatus.ResumeFailed,
                     StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(value =>
                value.Status == GenerationRunStatuses.WaitingForInput &&
                !value.PendingWorkItems.Any(item =>
                    item.Blocking &&
                    item.Status == "pending") &&
                value.ResumeFailureCount >=
                GenerationRunPresentationStatus.ResumeFailureAttentionThreshold);
        }
        else if (string.Equals(
                     status,
                     GenerationRunStatuses.WaitingForInput,
                     StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(value =>
                value.Status == GenerationRunStatuses.WaitingForInput &&
                value.PendingWorkItems.Any(item =>
                    item.Blocking &&
                    item.Status == "pending"));
        }
        else if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(value => value.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(stage))
            query = query.Where(value => value.Stages.Any(item => item.StageKey == stage));
        if (!string.IsNullOrWhiteSpace(failureDisposition))
            query = query.Where(value => value.FailureDisposition == failureDisposition);

        var totalCount = await query.CountAsync(cancellationToken);
        var runs = await query
            .AsSplitQuery()
            .Include(value => value.GenerationProject)
                .ThenInclude(value => value.Customer)
            .Include(value => value.Stages)
            .Include(value => value.PendingWorkItems)
            .OrderByDescending(value => value.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new AdminGenerationRunListViewModel(
            runs.Select(MapListItem).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<AdminGenerationRunDiagnosticsViewModel?> GetAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await controlPlane.GenerationRuns
            .AsNoTracking()
            .AsSplitQuery()
            .Include(value => value.GenerationProject)
                .ThenInclude(value => value.Customer)
            .Include(value => value.GenerationProject)
                .ThenInclude(value => value.Tenant)
                .ThenInclude(value => value!.Environments)
            .Include(value => value.EvidenceSnapshots)
            .Include(value => value.Stages)
            .Include(value => value.AgentWorkItems)
                .ThenInclude(value => value.Attempts)
                    .ThenInclude(value => value.ModelCalls)
            .Include(value => value.ArtifactVersions)
            .Include(value => value.Events)
            .Include(value => value.ModelCalls)
            .Include(value => value.PendingWorkItems)
            .Include(value => value.BundleVersions)
                .ThenInclude(value => value.Deployments)
            .SingleOrDefaultAsync(value => value.Id == runId, cancellationToken);
        if (run is null) return null;

        var snapshot = run.EvidenceSnapshots
            .OrderByDescending(value => value.Sequence)
            .FirstOrDefault();
        var runtimeUrls = run.GenerationProject.Tenant?.Environments.ToDictionary(
            value => value.Environment,
            value => value.RuntimeUrl,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string?>();
        var deployments = run.BundleVersions
            .SelectMany(value => value.Deployments)
            .OrderByDescending(value => value.UpdatedAt)
            .Select(value => new AdminDeploymentViewModel(
                value.Id,
                value.Environment,
                value.Status,
                runtimeUrls.GetValueOrDefault(value.Environment),
                value.TestsStatus,
                value.AttemptCount,
                value.FailureCode,
                value.FailureMessage,
                value.UpdatedAt))
            .ToArray();
        var now = clock.GetUtcNow();
        var status = GenerationRunPresentationStatus.For(run);
        var resumeFailed =
            status == GenerationRunPresentationStatus.ResumeFailed;
        return new AdminGenerationRunDiagnosticsViewModel(
            run.Id,
            run.GenerationProjectId,
            run.GenerationProject.CustomerId,
            run.GenerationProject.CompanyName,
            run.GenerationProject.Customer.Email ?? string.Empty,
            status,
            run.RequestedMode,
            run.ActualMode,
            run.ClaimCount,
            resumeFailed ? run.ResumeFailureCount : run.FailureCount,
            resumeFailed ? "recoverable" : run.FailureDisposition,
            resumeFailed ? "generation_resume_failed" : run.FailureCode,
            resumeFailed
                ? run.ResumeFailureMessage
                : run.FailureMessage,
            run.NextAttemptAt,
            MaskLeaseOwner(run.LeaseOwner),
            run.LeaseExpiresAt,
            run.HeartbeatAt,
            run.CreatedAt,
            run.UpdatedAt,
            run.StartedAt,
            run.CompletedAt,
            run.CancellationRequestedAt,
            run.CancelledAt,
            snapshot?.Sequence,
            snapshot?.ContentSha256,
            snapshot?.ItemCount ?? 0,
            CanRetryNow(run, now),
            CanCancel(run),
            run.Status == GenerationRunStatuses.Cancelled,
            run.Stages.OrderBy(value => value.Sequence)
                .Select(value => new AdminGenerationStageViewModel(
                    value.Id,
                    value.StageKey,
                    value.Status,
                    value.Sequence,
                    value.AttemptCount,
                    value.InputHash,
                    value.Summary,
                    value.FailureCode,
                    value.FailureMessage,
                    value.StartedAt,
                    value.CompletedAt))
                .ToArray(),
            run.AgentWorkItems
                .OrderBy(value => value.Sequence)
                .ThenBy(value => value.CreatedAt)
                .Select(value => new AdminAgentWorkItemViewModel(
                    value.Id,
                    value.GenerationRunStageId,
                    value.ParentWorkItemId,
                    value.WorkItemKey,
                    value.AgentKey,
                    value.AgentVersion,
                    value.AgentRole,
                    value.AgentLayer,
                    value.ContractVersion,
                    value.DefinitionHash,
                    value.SupervisorAgentKey,
                    value.Status,
                    value.AttemptCount,
                    value.RepairCount,
                    value.RoutingDepth,
                    value.InputManifestHash,
                    value.Summary,
                    value.FailureDisposition,
                    value.FailureCode,
                    value.FailureMessage,
                    value.LeaseExpiresAt,
                    value.HeartbeatAt,
                    value.StartedAt,
                    value.CompletedAt,
                    value.Attempts
                        .OrderBy(attempt => attempt.AttemptNumber)
                        .Select(attempt => new AdminAgentExecutionAttemptViewModel(
                            attempt.Id,
                            attempt.AttemptNumber,
                            attempt.Status,
                            attempt.AgentKey,
                            attempt.AgentVersion,
                            attempt.DefinitionHash,
                            attempt.InputManifestHash,
                            attempt.OutputArtifactHash,
                            attempt.FailureDisposition,
                            attempt.FailureCode,
                            attempt.FailureMessage,
                            attempt.RepairInstructions,
                            attempt.ModelCalls.Count,
                            attempt.StartedAt,
                            attempt.CompletedAt))
                        .ToArray()))
                .ToArray(),
            run.ArtifactVersions
                .OrderBy(value => value.ArtifactKey)
                .ThenBy(value => value.Version)
                .Select(value => new AdminGenerationArtifactVersionViewModel(
                    value.Id,
                    value.GenerationAgentWorkItemId,
                    value.GenerationAgentExecutionAttemptId,
                    value.SupersedesArtifactVersionId,
                    value.ArtifactKey,
                    value.ArtifactType,
                    value.SchemaName,
                    value.SchemaVersion,
                    value.Version,
                    value.ContentHash,
                    value.InputManifestHash,
                    value.EvidenceSnapshotHash,
                    value.ProducerAgentKey,
                    value.ProducerAgentVersion,
                    value.ProducerDefinitionHash,
                    value.ValidationStatus,
                    value.ValidationSummary,
                    value.CreatedAt))
                .ToArray(),
            run.Events.OrderBy(value => value.Sequence)
                .Select(value => new AdminGenerationRunEventViewModel(
                    value.Id,
                    value.Sequence,
                    value.EventType,
                    value.StageKey,
                    value.AttemptNumber,
                    value.Summary,
                    value.SafeMetadataJson,
                    value.CreatedAt))
                .ToArray(),
            run.ModelCalls.OrderBy(value => value.CreatedAt)
                .Select(value => new AdminGenerationModelCallViewModel(
                    value.Id,
                    value.GenerationAgentWorkItemId,
                    value.GenerationAgentExecutionAttemptId,
                    value.Phase,
                    value.Status,
                    value.RequestHash,
                    value.ProviderResponseId,
                    value.AgentKey,
                    value.AgentVersion,
                    value.Provider,
                    value.Model,
                    value.PromptVersion,
                    value.ContractVersion,
                    value.DurationMilliseconds,
                    value.InputTokenCount,
                    value.OutputTokenCount,
                    value.ResponseHash,
                    value.FailureCode,
                    value.CreatedAt,
                    value.UpdatedAt))
                .ToArray(),
            run.PendingWorkItems.OrderBy(value => value.CreatedAt)
                .Select(value => new AdminPendingWorkViewModel(
                    value.Id,
                    value.Kind,
                    value.Status,
                    value.Blocking,
                    value.Prompt,
                    value.TargetRole,
                    value.CreatedAt))
                .ToArray(),
            deployments);
    }

    public async Task<AdminGenerationRunActionViewModel> RetryNowAsync(
        Guid actorId,
        string actorEmail,
        Guid runId,
        string reason,
        CancellationToken cancellationToken)
    {
        reason = RequiredReason(reason);
        IDbContextTransaction? transaction = null;
        try
        {
            if (controlPlane.Database.IsRelational())
            {
                transaction = await controlPlane.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            }

            var run = await controlPlane.GenerationRuns
                .Include(value => value.GenerationProject)
                .Include(value => value.Events)
                .SingleOrDefaultAsync(value => value.Id == runId, cancellationToken)
                ?? throw new KeyNotFoundException("Generation run was not found.");
            var now = clock.GetUtcNow();
            if (!CanRetryNow(run, now))
            {
                throw new InvalidOperationException(
                    "Only an unleased recoverable or configuration failure, or a scheduled retry, can be retried now.");
            }

            GenerationRunTransitionPolicy.EnsureAllowed(
                run.Status,
                GenerationRunStatuses.Queued);
            run.Status = GenerationRunStatuses.Queued;
            run.NextAttemptAt = now;
            run.CompletedAt = null;
            run.LeaseId = null;
            run.LeaseOwner = null;
            run.LeaseExpiresAt = null;
            run.GenerationProject.Status = "generating";
            GenerationRunEventFactory.Add(
                controlPlane,
                run,
                GenerationRunEventTypes.AdminRetryRequested,
                "An administrator requested an immediate retry.");
            AddAudit(
                actorId,
                actorEmail,
                run,
                "generation_run_retry_requested",
                reason);
            await controlPlane.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return new AdminGenerationRunActionViewModel(
                run.Id,
                run.Status,
                run.NextAttemptAt);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public async Task<AdminGenerationRunActionViewModel> CancelAsync(
        Guid actorId,
        string actorEmail,
        Guid runId,
        string reason,
        CancellationToken cancellationToken)
    {
        reason = RequiredReason(reason);
        await using var transaction = controlPlane.Database.IsRelational()
            ? await controlPlane.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;
        var run = await controlPlane.GenerationRuns
            .Include(value => value.GenerationProject)
            .Include(value => value.Stages)
            .Include(value => value.AgentWorkItems)
                .ThenInclude(value => value.Attempts)
            .Include(value => value.Events)
            .SingleOrDefaultAsync(value => value.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException("Generation run was not found.");
        if (!CanCancel(run))
            throw new InvalidOperationException(
                "Only queued, running, or waiting generation can be cancelled.");

        var now = clock.GetUtcNow();
        GenerationRunEventFactory.Add(
            controlPlane,
            run,
            GenerationRunEventTypes.AdminCancellationRequested,
            "An administrator requested generation cancellation.");
        run.CancellationRequestedAt = now;
        run.NextAttemptAt = null;
        run.LeaseId = null;
        run.LeaseOwner = null;
        run.LeaseExpiresAt = null;
        run.HeartbeatAt = now;

        if (run.Status == GenerationRunStatuses.Running)
        {
            GenerationRunTransitionPolicy.EnsureAllowed(
                run.Status,
                GenerationRunStatuses.CancellationRequested);
            run.Status = GenerationRunStatuses.CancellationRequested;
            run.GenerationProject.Status = "cancelling";
        }
        else
        {
            GenerationRunTransitionPolicy.EnsureAllowed(
                run.Status,
                GenerationRunStatuses.Cancelled);
            run.Status = GenerationRunStatuses.Cancelled;
            run.CancelledAt = now;
            run.CompletedAt = now;
            run.GenerationProject.Status = "cancelled";
            GenerationLeaseService.CancelAgentWorkItems(run, now);
            foreach (var stage in run.Stages.Where(value =>
                         value.Status is GenerationStageStatuses.Running or
                             GenerationStageStatuses.WaitingForInput))
            {
                stage.Status = GenerationStageStatuses.Cancelled;
                stage.CompletedAt = now;
                stage.FailureCode = null;
                stage.FailureMessage = null;
            }
            GenerationRunEventFactory.Add(
                controlPlane,
                run,
                GenerationRunEventTypes.Cancelled,
                "Generation was cancelled before another stage started.");
        }

        AddAudit(
            actorId,
            actorEmail,
            run,
            "generation_run_cancellation_requested",
            reason);
        await controlPlane.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new AdminGenerationRunActionViewModel(
            run.Id,
            GenerationRunPresentationStatus.For(run),
            run.NextAttemptAt);
    }

    public async Task<AdminGenerationRunActionViewModel> ResumeAsync(
        Guid actorId,
        string actorEmail,
        Guid runId,
        string reason,
        CancellationToken cancellationToken)
    {
        reason = RequiredReason(reason);
        await using var transaction = controlPlane.Database.IsRelational()
            ? await controlPlane.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;
        var run = await controlPlane.GenerationRuns
            .Include(value => value.GenerationProject)
            .Include(value => value.Events)
            .SingleOrDefaultAsync(value => value.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException("Generation run was not found.");
        if (run.Status != GenerationRunStatuses.Cancelled)
            throw new InvalidOperationException(
                "Only cancelled generation can be resumed.");

        var now = clock.GetUtcNow();
        GenerationRunTransitionPolicy.EnsureAllowed(
            run.Status,
            GenerationRunStatuses.Queued);
        run.Status = GenerationRunStatuses.Queued;
        run.NextAttemptAt = now;
        run.CompletedAt = null;
        run.CancellationRequestedAt = null;
        run.CancelledAt = null;
        run.FailureDisposition = null;
        run.FailureCode = null;
        run.FailureMessage = null;
        run.LeaseId = null;
        run.LeaseOwner = null;
        run.LeaseExpiresAt = null;
        run.GenerationProject.Status = "generating";
        GenerationRunEventFactory.Add(
            controlPlane,
            run,
            GenerationRunEventTypes.AdminResumeRequested,
            "An administrator resumed cancelled generation.");
        AddAudit(
            actorId,
            actorEmail,
            run,
            "generation_run_resume_requested",
            reason);
        await controlPlane.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new AdminGenerationRunActionViewModel(
            run.Id,
            run.Status,
            run.NextAttemptAt);
    }

    private static AdminGenerationRunListItemViewModel MapListItem(GenerationRun run)
    {
        var status = GenerationRunPresentationStatus.For(run);
        var resumeFailed =
            status == GenerationRunPresentationStatus.ResumeFailed;
        var currentStage = run.Stages
            .Where(value => value.Status != GenerationStageStatuses.Completed)
            .OrderByDescending(value => value.Sequence)
            .Select(value => value.StageKey)
            .FirstOrDefault() ??
            run.Stages.OrderByDescending(value => value.Sequence)
                .Select(value => value.StageKey)
                .FirstOrDefault();
        return new AdminGenerationRunListItemViewModel(
            run.Id,
            run.GenerationProjectId,
            run.GenerationProject.CustomerId,
            run.GenerationProject.CompanyName,
            run.GenerationProject.Customer.Email ?? string.Empty,
            status,
            run.RequestedMode,
            run.ActualMode,
            currentStage,
            run.ClaimCount,
            resumeFailed ? run.ResumeFailureCount : run.FailureCount,
            resumeFailed ? "recoverable" : run.FailureDisposition,
            resumeFailed ? "generation_resume_failed" : run.FailureCode,
            run.NextAttemptAt,
            run.HeartbeatAt,
            run.CreatedAt,
            run.UpdatedAt);
    }

    private static bool CanRetryNow(GenerationRun run, DateTimeOffset now) =>
        run.FailureDisposition is
            GenerationFailureDispositions.Retryable or
            GenerationFailureDispositions.Configuration &&
        (run.LeaseId is null || run.LeaseExpiresAt < now) &&
        (run.Status == GenerationRunStatuses.Failed ||
         (run.Status == GenerationRunStatuses.Queued && run.NextAttemptAt > now));

    private static bool CanCancel(GenerationRun run) =>
        run.Status is GenerationRunStatuses.Queued or
            GenerationRunStatuses.Running or
            GenerationRunStatuses.WaitingForInput;

    private void AddAudit(
        Guid actorId,
        string actorEmail,
        GenerationRun run,
        string action,
        string reason)
    {
        controlPlane.AdminAuditRecords.Add(new AdminAuditRecord
        {
            ActorAdminAccountId = actorId,
            ActorEmailSnapshot = actorEmail,
            Action = action,
            ResourceType = "generation_run",
            ResourceId = run.Id.ToString(),
            SafeDetailsJson = JsonSerializer.Serialize(new
            {
                run.GenerationProjectId,
                Reason = reason
            })
        });
    }

    private static string RequiredReason(string value)
    {
        var reason = value?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 1_000)
            throw new ArgumentException(
                "An administrative reason between 3 and 1000 characters is required.");
        return reason;
    }

    private static string? MaskLeaseOwner(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.LastIndexOf(':');
        return separator <= 0 ? "active worker" : value[..(separator + 1)] + "…";
    }
}
