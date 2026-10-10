using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed class GenerationRunOrchestrator(
    ErpGeneratorDbContext dbContext,
    IGenerationLeaseService leaseService,
    IAgentDefinitionRegistry agentRegistry,
    IAgentWorkItemService agentWorkItemService,
    IEnumerable<IGenerationStageHandler> stageHandlers,
    IServiceScopeFactory scopeFactory,
    IOptions<GenerationOrchestrationOptions> options,
    TimeProvider timeProvider,
    ILogger<GenerationRunOrchestrator> logger) : IGenerationRunOrchestrator
{
    private readonly IGenerationStageHandler[] orderedStages =
        stageHandlers.OrderBy(value => value.Sequence).ToArray();

    public async Task<bool> TryProcessNextAsync(
        string workerId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await leaseService.RecoverExpiredAsync(now, cancellationToken);
        var claim = await leaseService.ClaimNextAsync(workerId, now, cancellationToken);
        if (claim is null) return false;

        dbContext.ChangeTracker.Clear();
        var run = await LoadRunAsync(claim.RunId, cancellationToken);
        if (GenerationRoutingPolicy.ForRunStatus(run.Status) == GenerationRoute.Cancel)
        {
            await TryFinalizeCancellationAsync(
                run.Id,
                cancellationToken);
            return true;
        }
        if (run.LeaseId != claim.LeaseId ||
            run.Status != GenerationRunStatuses.Running)
        {
            logger.LogWarning(
                "Generation run {RunId} was claimed but the lease could not be verified.",
                claim.RunId);
            return true;
        }

        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.LeaseClaimed,
            "A worker claimed the generation run.",
            attemptNumber: run.ClaimCount);
        await dbContext.SaveChangesAsync(cancellationToken);

        GenerationRunStage? currentStage = null;
        AgentWorkItemExecution? currentExecution = null;
        GenerationEvidenceSnapshot? currentSnapshot = null;
        try
        {
            if (orderedStages.Length == 0)
                throw new InvalidOperationException("No generation stages are registered.");

            object? previousOutput = null;
            var snapshot = run.EvidenceSnapshots
                .OrderByDescending(value => value.Sequence)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Generation evidence snapshot was not captured.");

            foreach (var handler in orderedStages)
            {
                currentSnapshot = snapshot;
                var agentDefinition = agentRegistry.GetForStage(handler.StageKey);
                if (!await leaseService.RenewAsync(
                        run.Id,
                        claim.LeaseId,
                        timeProvider.GetUtcNow(),
                        cancellationToken))
                {
                    if (!await TryFinalizeCancellationAsync(
                            run.Id,
                            cancellationToken))
                    {
                        logger.LogWarning(
                            "Generation run {RunId} lost its lease before stage {StageKey}.",
                            run.Id,
                            handler.StageKey);
                    }
                    return true;
                }

                var inputHash = InputManifestHash(
                    run,
                    handler.StageKey,
                    snapshot,
                    agentDefinition,
                    previousOutput);
                currentStage = run.Stages.FirstOrDefault(value =>
                    value.StageKey == handler.StageKey);
                if (currentStage is not null &&
                    currentStage.Status == GenerationStageStatuses.Completed &&
                    string.Equals(
                        currentStage.InputHash,
                        inputHash,
                        StringComparison.Ordinal))
                {
                    var restored = await handler.RestoreCompletedResultAsync(
                        new GenerationStageContext(
                            run,
                            currentStage,
                            snapshot,
                            previousOutput,
                            null),
                        cancellationToken);
                    if (restored is not null)
                    {
                        GenerationRunEventFactory.Add(
                            dbContext,
                            run,
                            GenerationRunEventTypes.StageReused,
                            $"Stage '{handler.StageKey}' reused its completed durable result.",
                            handler.StageKey,
                            currentStage.AttemptCount);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        previousOutput = restored.Output;
                        continue;
                    }
                }

                currentStage = StartStage(run, handler, inputHash);
                if (currentStage.CreatedAt == default &&
                    dbContext.Entry(currentStage).State != EntityState.Added)
                {
                    dbContext.GenerationRunStages.Add(currentStage);
                }
                var previousFailureMessage =
                    currentStage.FailureCode == "generated_bundle_invalid"
                        ? currentStage.FailureMessage
                        : null;
                currentStage.FailureCode = null;
                currentStage.FailureMessage = null;
                GenerationRunEventFactory.Add(
                    dbContext,
                    run,
                    GenerationRunEventTypes.StageStarted,
                    $"Stage '{handler.StageKey}' started.",
                    handler.StageKey,
                    currentStage.AttemptCount);
                await dbContext.SaveChangesAsync(cancellationToken);

                currentExecution = await agentWorkItemService.StartAsync(
                    run,
                    currentStage,
                    snapshot,
                    agentDefinition,
                    inputHash,
                    workerId,
                    claim.LeaseId,
                    cancellationToken);
                var result = await ExecuteWithHeartbeatAsync(
                    run.Id,
                    claim.LeaseId,
                    token => handler.ExecuteAsync(
                        new GenerationStageContext(
                            run,
                            currentStage,
                            snapshot,
                            previousOutput,
                            previousFailureMessage),
                        token),
                    cancellationToken);

                if (!await leaseService.RenewAsync(
                        run.Id,
                        claim.LeaseId,
                        timeProvider.GetUtcNow(),
                        cancellationToken))
                {
                    if (!await TryFinalizeCancellationAsync(
                            run.Id,
                            cancellationToken))
                    {
                        logger.LogWarning(
                            "Generation run {RunId} lost its lease while stage {StageKey} was executing.",
                            run.Id,
                            handler.StageKey);
                        dbContext.ChangeTracker.Clear();
                    }
                    return true;
                }

                await agentWorkItemService.CompleteAsync(
                    currentExecution,
                    snapshot,
                    result,
                    cancellationToken);
                currentExecution = null;
                if (GenerationRoutingPolicy.ForStageOutcome(result.Outcome) ==
                    GenerationRoute.WaitForInput)
                {
                    currentStage.Status = GenerationStageStatuses.WaitingForInput;
                    currentStage.Summary = result.Summary.Length <= 4_000
                        ? result.Summary
                        : result.Summary[..4_000];
                    currentStage.CompletedAt = null;
                    GenerationRunTransitionPolicy.EnsureAllowed(
                        run.Status,
                        GenerationRunStatuses.WaitingForInput);
                    run.Status = GenerationRunStatuses.WaitingForInput;
                    run.GenerationProject.Status = "needs_information";
                    Release(run);
                    GenerationRunEventFactory.Add(
                        dbContext,
                        run,
                        GenerationRunEventTypes.WaitingForInput,
                        "Generation is waiting for required company information.",
                        handler.StageKey,
                        currentStage.AttemptCount);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return true;
                }

                CompleteStage(currentStage, result.Summary);
                GenerationRunEventFactory.Add(
                    dbContext,
                    run,
                    GenerationRunEventTypes.StageCompleted,
                    $"Stage '{handler.StageKey}' completed.",
                    handler.StageKey,
                    currentStage.AttemptCount);
                previousOutput = result.Output;
            }

            GenerationRunTransitionPolicy.EnsureAllowed(
                run.Status,
                GenerationRunStatuses.Completed);
            run.Status = GenerationRunStatuses.Completed;
            run.CompletedAt = timeProvider.GetUtcNow();
            run.FailureCode = null;
            run.FailureMessage = null;
            run.FailureDisposition = null;
            run.NextAttemptAt = null;
            run.GenerationProject.Status = "generated";
            Release(run);
            GenerationRunEventFactory.Add(
                dbContext,
                run,
                GenerationRunEventTypes.Completed,
                "ERP generation completed.");
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            if (!await TryFinalizeCancellationAsync(
                    run.Id,
                    cancellationToken))
            {
                await HandleFailureAsync(
                    run,
                    currentStage,
                    currentExecution,
                    currentSnapshot,
                    claim.LeaseId,
                    exception,
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            await HandleFailureAsync(
                run,
                currentStage,
                currentExecution,
                currentSnapshot,
                claim.LeaseId,
                exception,
                cancellationToken);
        }

        return true;
    }

    private async Task HandleFailureAsync(
        GenerationRun run,
        GenerationRunStage? stage,
        AgentWorkItemExecution? execution,
        GenerationEvidenceSnapshot? evidenceSnapshot,
        Guid leaseId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            exception,
            "Generation run {RunId} failed during stage {StageKey} on claim {ClaimCount}.",
            run.Id,
            stage?.StageKey,
            run.ClaimCount);
        if (!await leaseService.RenewAsync(
                run.Id,
                leaseId,
                timeProvider.GetUtcNow(),
                cancellationToken))
        {
            if (await TryFinalizeCancellationAsync(
                    run.Id,
                    cancellationToken))
            {
                return;
            }
            logger.LogWarning(
                "Generation run {RunId} failure was not persisted because its lease was lost.",
                run.Id);
            dbContext.ChangeTracker.Clear();
            return;
        }

        var failure = GenerationFailureClassifier.Classify(exception);
        SupervisorDecision? supervisorDecision = null;
        if (execution is not null && evidenceSnapshot is not null)
        {
            supervisorDecision = await agentWorkItemService.FailAsync(
                execution,
                evidenceSnapshot,
                failure,
                cancellationToken);
        }
        run.FailureCount += 1;
        run.FailureDisposition = failure.Disposition;
        run.FailureCode = failure.Code;
        run.FailureMessage = failure.CustomerMessage;
        if (stage is not null)
        {
            stage.Status = GenerationStageStatuses.Failed;
            stage.FailureCode = failure.Code;
            stage.FailureMessage = failure.AdminMessage;
            stage.CompletedAt = timeProvider.GetUtcNow();
        }

        var settings = options.Value;
        var maximumAttempts = Math.Clamp(settings.MaximumAttempts, 1, 20);
        var route =
            supervisorDecision?.Decision == SupervisorDecisionTypes.RepairSameAgent
                ? GenerationRoutingPolicy.ForFailure(
                    failure,
                    run.FailureCount,
                    maximumAttempts)
                : execution is null
                    ? GenerationRoutingPolicy.ForFailure(
                        failure,
                        run.FailureCount,
                        maximumAttempts)
                    : GenerationRoute.Fail;
        if (route == GenerationRoute.Retry)
        {
            GenerationRunTransitionPolicy.EnsureAllowed(
                run.Status,
                GenerationRunStatuses.Queued);
            run.Status = GenerationRunStatuses.Queued;
            run.NextAttemptAt = timeProvider.GetUtcNow().Add(RetryDelay(run.FailureCount));
            run.GenerationProject.Status = "generating";
            Release(run);
            GenerationRunEventFactory.Add(
                dbContext,
                run,
                GenerationRunEventTypes.RetryScheduled,
                "A retryable generation failure was scheduled for another attempt.",
                stage?.StageKey,
                stage?.AttemptCount,
                $$"""{"failureCode":"{{failure.Code}}"}""");
        }
        else
        {
            GenerationRunTransitionPolicy.EnsureAllowed(
                run.Status,
                GenerationRunStatuses.Failed);
            run.Status = GenerationRunStatuses.Failed;
            run.CompletedAt = timeProvider.GetUtcNow();
            run.NextAttemptAt = null;
            run.GenerationProject.Status = "failed";
            Release(run);
            GenerationRunEventFactory.Add(
                dbContext,
                run,
                GenerationRunEventTypes.Failed,
                failure.Disposition == GenerationFailureDispositions.Configuration
                    ? "Generation stopped because administrator configuration is required."
                    : "Generation stopped after exhausting safe recovery.",
                stage?.StageKey,
                stage?.AttemptCount,
                $$"""{"failureCode":"{{failure.Code}}","disposition":"{{failure.Disposition}}"}""");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<T> ExecuteWithHeartbeatAsync<T>(
        Guid runId,
        Guid leaseId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(runId, leaseId, linked, cancellationToken);
        try
        {
            return await operation(linked.Token);
        }
        finally
        {
            linked.Cancel();
            await heartbeat;
        }
    }

    private async Task HeartbeatAsync(
        Guid runId,
        Guid leaseId,
        CancellationTokenSource operation,
        CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            options.Value.HeartbeatIntervalSeconds,
            5,
            3_600));
        try
        {
            while (!operation.IsCancellationRequested)
            {
                await Task.Delay(interval, operation.Token);
                await using var scope = scopeFactory.CreateAsyncScope();
                var heartbeatLease = scope.ServiceProvider
                    .GetRequiredService<IGenerationLeaseService>();
                if (await heartbeatLease.RenewAsync(
                        runId,
                        leaseId,
                        timeProvider.GetUtcNow(),
                        operation.Token))
                {
                    continue;
                }

                logger.LogWarning(
                    "Generation run {RunId} heartbeat detected a lost lease.",
                    runId);
                operation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (
            operation.IsCancellationRequested ||
            stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Generation run {RunId} heartbeat failed.",
                runId);
            operation.Cancel();
        }
    }

    private async Task<GenerationRun> LoadRunAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        await dbContext.GenerationRuns
            .AsSplitQuery()
            .Include(value => value.GenerationProject)
                .ThenInclude(value => value.Participants)
            .Include(value => value.GenerationProject)
                .ThenInclude(value => value.InterviewRequests)
                .ThenInclude(value => value.Answers)
            .Include(value => value.EvidenceSnapshots)
                .ThenInclude(value => value.Items)
            .Include(value => value.PendingWorkItems)
            .Include(value => value.Stages)
            .Include(value => value.Events)
            .SingleAsync(value => value.Id == runId, cancellationToken);

    private GenerationRunStage StartStage(
        GenerationRun run,
        IGenerationStageHandler handler,
        string inputHash)
    {
        var now = timeProvider.GetUtcNow();
        var stage = run.Stages.FirstOrDefault(value =>
            value.StageKey == handler.StageKey);
        if (stage is null)
        {
            stage = new GenerationRunStage
            {
                GenerationRun = run,
                GenerationRunId = run.Id,
                StageKey = handler.StageKey,
                Sequence = handler.Sequence
            };
            run.Stages.Add(stage);
        }

        stage.Status = GenerationStageStatuses.Running;
        stage.AttemptCount += 1;
        stage.InputHash = inputHash;
        stage.StartedAt = now;
        stage.CompletedAt = null;
        return stage;
    }

    private void CompleteStage(GenerationRunStage stage, string summary)
    {
        stage.Status = GenerationStageStatuses.Completed;
        stage.Summary = summary.Length <= 4_000 ? summary : summary[..4_000];
        stage.FailureCode = null;
        stage.FailureMessage = null;
        stage.CompletedAt = timeProvider.GetUtcNow();
    }

    private void Release(GenerationRun run)
    {
        run.HeartbeatAt = timeProvider.GetUtcNow();
        run.LeaseId = null;
        run.LeaseOwner = null;
        run.LeaseExpiresAt = null;
    }

    private static string InputManifestHash(
        GenerationRun run,
        string stageKey,
        GenerationEvidenceSnapshot snapshot,
        AgentDefinition definition,
        object? previousOutput)
    {
        var input =
            $"{stageKey}|{snapshot.ContentSha256}|{run.RequestedMode}|" +
            $"{run.GenerationProject.GenerateWithAssumptions}|" +
            $"{run.GenerationProject.Goal}|{definition.DefinitionHash}|" +
            $"{System.Text.Json.JsonSerializer.Serialize(previousOutput)}";
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private TimeSpan RetryDelay(int failureCount)
    {
        var settings = options.Value;
        var initial = Math.Clamp(settings.InitialRetryDelaySeconds, 1, 86_400);
        var maximum = Math.Clamp(
            settings.MaximumRetryDelaySeconds,
            initial,
            86_400);
        var multiplier = Math.Pow(2, Math.Max(0, failureCount - 1));
        return TimeSpan.FromSeconds(Math.Min(maximum, initial * multiplier));
    }

    private async Task<bool> TryFinalizeCancellationAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var now = timeProvider.GetUtcNow();
        await leaseService.RecoverExpiredAsync(now, cancellationToken);
        dbContext.ChangeTracker.Clear();
        return await dbContext.GenerationRuns
            .AsNoTracking()
            .AnyAsync(
                value =>
                    value.Id == runId &&
                    value.Status == GenerationRunStatuses.Cancelled &&
                    value.CancellationRequestedAt != null,
                cancellationToken);
    }
}
