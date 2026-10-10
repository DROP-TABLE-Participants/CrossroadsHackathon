using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed class AgentWorkItemService(
    ErpGeneratorDbContext dbContext,
    IAgentDefinitionRegistry registry,
    IAgentExecutionContextAccessor executionContextAccessor,
    IOptions<GenerationOrchestrationOptions> options,
    TimeProvider timeProvider) : IAgentWorkItemService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<AgentWorkItemExecution> StartAsync(
        GenerationRun run,
        GenerationRunStage stage,
        GenerationEvidenceSnapshot evidenceSnapshot,
        AgentDefinition definition,
        string inputManifestHash,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        var workItemKey = $"{stage.StageKey}:primary";
        var workItem = await dbContext.GenerationAgentWorkItems
            .Include(value => value.Dependencies)
            .Include(value => value.Attempts)
            .SingleOrDefaultAsync(
                value =>
                    value.GenerationRunId == run.Id &&
                    value.WorkItemKey == workItemKey,
                cancellationToken);
        if (workItem is null)
        {
            workItem = CreateWorkItem(
                run,
                stage,
                definition,
                workItemKey,
                inputManifestHash,
                null);
            dbContext.GenerationAgentWorkItems.Add(workItem);
            await AddDependenciesAsync(workItem, definition, cancellationToken);
        }
        else
        {
            EnsureDefinitionMatches(workItem, definition);
            workItem.InputManifestHash = inputManifestHash;
        }

        await EnsureDependenciesCompletedAsync(workItem, cancellationToken);
        var attemptsForInput = workItem.Attempts.Count(value =>
            value.InputManifestHash == inputManifestHash);
        var maximumAttempts = Math.Min(
            definition.MaximumAttempts,
            Math.Clamp(options.Value.MaximumAgentAttempts, 1, 20));
        if (attemptsForInput >= maximumAttempts)
        {
            throw new AgentExecutionBudgetExceededException(
                $"Agent '{definition.Key}' exhausted its bounded attempt budget.");
        }

        var now = timeProvider.GetUtcNow();
        workItem.Status = AgentWorkItemStatuses.Running;
        workItem.AttemptCount += 1;
        workItem.LeaseOwner = workerId;
        workItem.LeaseId = leaseId;
        workItem.LeaseExpiresAt = now.AddSeconds(Math.Clamp(
            options.Value.LeaseDurationSeconds,
            30,
            86_400));
        workItem.HeartbeatAt = now;
        workItem.StartedAt ??= now;
        workItem.CompletedAt = null;
        workItem.FailureCode = null;
        workItem.FailureMessage = null;
        workItem.FailureDisposition = null;

        var prerequisiteArtifacts = await PrerequisiteArtifactReferencesAsync(
            run.Id,
            definition,
            cancellationToken);
        var inputEnvelope = new AgentInputEnvelope(
            run.Id,
            stage.Id,
            definition.Key,
            definition.Version,
            definition.OutputSchemaVersion,
            inputManifestHash,
            evidenceSnapshot.ContentSha256,
            EvidenceReferences(evidenceSnapshot),
            prerequisiteArtifacts);
        var attempt = new GenerationAgentExecutionAttempt
        {
            GenerationAgentWorkItem = workItem,
            GenerationAgentWorkItemId = workItem.Id,
            AttemptNumber = workItem.AttemptCount,
            Status = AgentAttemptStatuses.Running,
            WorkerId = workerId,
            LeaseId = leaseId,
            AgentKey = definition.Key,
            AgentVersion = definition.Version,
            DefinitionHash = definition.DefinitionHash,
            InputManifestHash = inputManifestHash,
            InputEnvelopeJson = JsonSerializer.Serialize(inputEnvelope, JsonOptions),
            StartedAt = now
        };
        workItem.Attempts.Add(attempt);
        dbContext.GenerationAgentExecutionAttempts.Add(attempt);
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.AgentWorkItemStarted,
            $"Agent work item '{definition.Key}' started.",
            stage.StageKey,
            attempt.AttemptNumber,
            SafeMetadata(definition.Key, workItem.Id, attempt.Id));
        await dbContext.SaveChangesAsync(cancellationToken);

        executionContextAccessor.Current = new AgentExecutionContext(
            workItem.Id,
            attempt.Id,
            definition.Key,
            definition.Version,
            definition.PromptVersion,
            definition.OutputSchemaVersion);
        return new AgentWorkItemExecution(workItem, attempt, definition);
    }

    public async Task CompleteAsync(
        AgentWorkItemExecution execution,
        GenerationEvidenceSnapshot evidenceSnapshot,
        GenerationStageResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var envelope = CreateOutputEnvelope(
                execution,
                evidenceSnapshot,
                result);
            var artifact = await CreateArtifactAsync(
                execution,
                evidenceSnapshot,
                execution.Definition.OutputArtifactType,
                execution.Definition.OutputSchemaName,
                execution.Definition.OutputSchemaVersion,
                envelope,
                result.Summary,
                cancellationToken);

            execution.Attempt.Status = AgentAttemptStatuses.Completed;
            execution.Attempt.OutputArtifactHash = artifact.ContentHash;
            execution.Attempt.CompletedAt = now;
            execution.WorkItem.Status =
                result.Outcome == GenerationStageOutcome.WaitingForInput
                    ? AgentWorkItemStatuses.WaitingForInput
                    : AgentWorkItemStatuses.Completed;
            execution.WorkItem.Summary = Bounded(result.Summary);
            execution.WorkItem.CompletedAt =
                result.Outcome == GenerationStageOutcome.Completed ? now : null;
            Release(execution.WorkItem, now);

            if (result.Output is GeneratedErpBundle bundle)
                await CreateBundleVersionAsync(execution, artifact, bundle, cancellationToken);

            var decision = result.Outcome == GenerationStageOutcome.WaitingForInput
                ? new SupervisorDecision(
                    SupervisorDecisionTypes.RequestEvidence,
                    "Required company evidence is missing.")
                : new SupervisorDecision(
                    SupervisorDecisionTypes.Accept,
                    "The typed output passed deterministic validation.");
            await RecordSupervisorDecisionAsync(
                execution,
                evidenceSnapshot,
                decision,
                cancellationToken);
            GenerationRunEventFactory.Add(
                dbContext,
                execution.WorkItem.GenerationRun,
                GenerationRunEventTypes.AgentWorkItemCompleted,
                $"Agent work item '{execution.Definition.Key}' completed.",
                execution.WorkItem.GenerationRunStage.StageKey,
                execution.Attempt.AttemptNumber,
                SafeMetadata(
                    execution.Definition.Key,
                    execution.WorkItem.Id,
                    execution.Attempt.Id));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            executionContextAccessor.Current = null;
        }
    }

    public async Task<SupervisorDecision> FailAsync(
        AgentWorkItemExecution execution,
        GenerationEvidenceSnapshot evidenceSnapshot,
        GenerationFailure failure,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            execution.Attempt.Status = AgentAttemptStatuses.Failed;
            execution.Attempt.FailureDisposition = failure.Disposition;
            execution.Attempt.FailureCode = failure.Code;
            execution.Attempt.FailureMessage = Bounded(failure.DiagnosticMessage);
            execution.Attempt.CompletedAt = now;
            execution.WorkItem.Status = AgentWorkItemStatuses.Failed;
            execution.WorkItem.FailureDisposition = failure.Disposition;
            execution.WorkItem.FailureCode = failure.Code;
            execution.WorkItem.FailureMessage = Bounded(failure.DiagnosticMessage);
            execution.WorkItem.CompletedAt = now;
            Release(execution.WorkItem, now);

            var repairLimit = Math.Min(
                execution.Definition.MaximumRepairs,
                Math.Clamp(options.Value.MaximumAgentRepairs, 0, 10));
            var attemptsForInput = execution.WorkItem.Attempts.Count(value =>
                value.InputManifestHash == execution.WorkItem.InputManifestHash);
            var attemptLimit = Math.Min(
                execution.Definition.MaximumAttempts,
                Math.Clamp(options.Value.MaximumAgentAttempts, 1, 20));
            var runAttemptLimit = Math.Clamp(options.Value.MaximumAttempts, 1, 20);
            var canRepair =
                failure.Disposition == GenerationFailureDispositions.Retryable &&
                attemptsForInput < attemptLimit &&
                execution.WorkItem.GenerationRun.FailureCount + 1 < runAttemptLimit &&
                execution.WorkItem.RepairCount < repairLimit &&
                execution.WorkItem.RoutingDepth <
                Math.Min(
                    execution.Definition.MaximumRoutingDepth,
                    Math.Clamp(options.Value.MaximumRoutingDepth, 0, 10));
            SupervisorDecision decision;
            if (canRepair)
            {
                execution.WorkItem.RepairCount += 1;
                execution.WorkItem.RoutingDepth += 1;
                execution.Attempt.RepairInstructions =
                    "Retry the same typed contract using the validation or transient failure as bounded repair context.";
                decision = new SupervisorDecision(
                    SupervisorDecisionTypes.RepairSameAgent,
                    "The failure is repairable within the configured budget.",
                    execution.Definition.Key,
                    execution.Attempt.RepairInstructions);
            }
            else
            {
                decision = new SupervisorDecision(
                    SupervisorDecisionTypes.Fail,
                    canRepair
                        ? "The supervisor rejected the output."
                        : "The bounded repair or routing budget is exhausted.");
            }

            await RecordSupervisorDecisionAsync(
                execution,
                evidenceSnapshot,
                decision,
                cancellationToken);
            GenerationRunEventFactory.Add(
                dbContext,
                execution.WorkItem.GenerationRun,
                GenerationRunEventTypes.AgentWorkItemFailed,
                $"Agent work item '{execution.Definition.Key}' failed.",
                execution.WorkItem.GenerationRunStage.StageKey,
                execution.Attempt.AttemptNumber,
                SafeMetadata(
                    execution.Definition.Key,
                    execution.WorkItem.Id,
                    execution.Attempt.Id));
            await dbContext.SaveChangesAsync(cancellationToken);
            return decision;
        }
        finally
        {
            executionContextAccessor.Current = null;
        }
    }

    private static GenerationAgentWorkItem CreateWorkItem(
        GenerationRun run,
        GenerationRunStage stage,
        AgentDefinition definition,
        string workItemKey,
        string inputManifestHash,
        GenerationAgentWorkItem? parent) =>
        new()
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            GenerationRunStage = stage,
            GenerationRunStageId = stage.Id,
            ParentWorkItem = parent,
            ParentWorkItemId = parent?.Id,
            WorkItemKey = workItemKey,
            AgentKey = definition.Key,
            AgentVersion = definition.Version,
            AgentRole = definition.Role,
            AgentLayer = definition.Layer,
            ContractVersion = definition.OutputSchemaVersion,
            DefinitionHash = definition.DefinitionHash,
            SupervisorAgentKey = definition.SupervisorAgentKey,
            Status = AgentWorkItemStatuses.Queued,
            Sequence = stage.Sequence,
            InputManifestHash = inputManifestHash
        };

    private async Task AddDependenciesAsync(
        GenerationAgentWorkItem workItem,
        AgentDefinition definition,
        CancellationToken cancellationToken)
    {
        if (definition.DependsOnAgentKeys.Count == 0) return;
        var dependencies = await dbContext.GenerationAgentWorkItems
            .Where(value =>
                value.GenerationRunId == workItem.GenerationRunId &&
                definition.DependsOnAgentKeys.Contains(value.AgentKey))
            .ToArrayAsync(cancellationToken);
        if (dependencies.Length != definition.DependsOnAgentKeys.Count)
            throw new InvalidOperationException(
                $"Agent '{definition.Key}' has an unsatisfied registered dependency.");
        foreach (var dependency in dependencies)
        {
            var edge = new GenerationAgentWorkItemDependency
            {
                GenerationAgentWorkItem = workItem,
                GenerationAgentWorkItemId = workItem.Id,
                DependsOnWorkItem = dependency,
                DependsOnWorkItemId = dependency.Id
            };
            workItem.Dependencies.Add(edge);
            dbContext.GenerationAgentWorkItemDependencies.Add(edge);
        }
    }

    private async Task EnsureDependenciesCompletedAsync(
        GenerationAgentWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (workItem.Dependencies.Count == 0) return;
        var dependencyIds = workItem.Dependencies
            .Select(value => value.DependsOnWorkItemId)
            .ToArray();
        var blocked = await dbContext.GenerationAgentWorkItems.AnyAsync(
            value =>
                dependencyIds.Contains(value.Id) &&
                value.Status != AgentWorkItemStatuses.Completed,
            cancellationToken);
        if (blocked)
            throw new InvalidOperationException(
                $"Agent work item '{workItem.WorkItemKey}' has incomplete dependencies.");
    }

    private async Task<IReadOnlyList<AgentArtifactReference>>
        PrerequisiteArtifactReferencesAsync(
            Guid runId,
            AgentDefinition definition,
            CancellationToken cancellationToken)
    {
        if (definition.DependsOnAgentKeys.Count == 0)
            return [];
        var artifacts = await dbContext.GenerationArtifactVersions
            .AsNoTracking()
            .Where(value =>
                value.GenerationRunId == runId &&
                definition.DependsOnAgentKeys.Contains(value.ProducerAgentKey))
            .OrderByDescending(value => value.Version)
            .ToArrayAsync(cancellationToken);
        return artifacts
            .GroupBy(value => value.ArtifactKey, StringComparer.Ordinal)
            .Select(value => value.First())
            .Select(value => new AgentArtifactReference(
                value.Id,
                value.ArtifactType,
                value.Version,
                value.ContentHash))
            .ToArray();
    }

    private async Task<GenerationArtifactVersion> CreateArtifactAsync(
        AgentWorkItemExecution execution,
        GenerationEvidenceSnapshot evidenceSnapshot,
        string artifactType,
        string schemaName,
        string schemaVersion,
        object value,
        string? validationSummary,
        CancellationToken cancellationToken)
    {
        var canonicalJson = JsonSerializer.Serialize(value, JsonOptions);
        var contentHash = Hash(canonicalJson);
        var existing = await dbContext.GenerationArtifactVersions
            .SingleOrDefaultAsync(
                artifact =>
                    artifact.GenerationRunId == execution.WorkItem.GenerationRunId &&
                    artifact.ArtifactType == artifactType &&
                    artifact.ContentHash == contentHash,
                cancellationToken);
        if (existing is not null) return existing;

        var artifactKey = artifactType;
        var previous = await dbContext.GenerationArtifactVersions
            .Where(value =>
                value.GenerationRunId == execution.WorkItem.GenerationRunId &&
                value.ArtifactKey == artifactKey)
            .OrderByDescending(value => value.Version)
            .FirstOrDefaultAsync(cancellationToken);
        var artifact = new GenerationArtifactVersion
        {
            GenerationRun = execution.WorkItem.GenerationRun,
            GenerationRunId = execution.WorkItem.GenerationRunId,
            GenerationRunStage = execution.WorkItem.GenerationRunStage,
            GenerationRunStageId = execution.WorkItem.GenerationRunStageId,
            GenerationAgentWorkItem = execution.WorkItem,
            GenerationAgentWorkItemId = execution.WorkItem.Id,
            GenerationAgentExecutionAttempt = execution.Attempt,
            GenerationAgentExecutionAttemptId = execution.Attempt.Id,
            SupersedesArtifactVersion = previous,
            SupersedesArtifactVersionId = previous?.Id,
            ArtifactKey = artifactKey,
            ArtifactType = artifactType,
            SchemaName = schemaName,
            SchemaVersion = schemaVersion,
            Version = (previous?.Version ?? 0) + 1,
            ContentHash = contentHash,
            InputManifestHash = execution.WorkItem.InputManifestHash,
            EvidenceSnapshotHash = evidenceSnapshot.ContentSha256,
            ProducerAgentKey = execution.Definition.Key,
            ProducerAgentVersion = execution.Definition.Version,
            ProducerDefinitionHash = execution.Definition.DefinitionHash,
            CanonicalJson = canonicalJson,
            ValidationStatus = "valid",
            ValidationSummary = Bounded(validationSummary)
        };
        dbContext.GenerationArtifactVersions.Add(artifact);
        GenerationRunEventFactory.Add(
            dbContext,
            execution.WorkItem.GenerationRun,
            GenerationRunEventTypes.ArtifactVersionCreated,
            $"Artifact '{artifact.ArtifactType}' version {artifact.Version} was recorded.",
            execution.WorkItem.GenerationRunStage.StageKey,
            execution.Attempt.AttemptNumber,
            JsonSerializer.Serialize(new
            {
                artifactId = artifact.Id,
                artifact.ArtifactType,
                artifact.Version,
                artifact.ContentHash,
                artifact.ProducerAgentKey
            }, JsonOptions));
        return artifact;
    }

    private async Task CreateBundleVersionAsync(
        AgentWorkItemExecution execution,
        GenerationArtifactVersion artifact,
        GeneratedErpBundle bundle,
        CancellationToken cancellationToken)
    {
        var bundleContentHash = Hash(JsonSerializer.Serialize(bundle, JsonOptions));
        if (await dbContext.ErpBundleVersions.AnyAsync(
                value =>
                    value.GenerationRunId == execution.WorkItem.GenerationRunId &&
                    value.ContentHash == bundleContentHash,
                cancellationToken))
        {
            return;
        }
        var bundleId =
            $"erp_{execution.WorkItem.GenerationRun.GenerationProject.Id:N}";
        var nextVersion = await dbContext.ErpBundleVersions
            .Where(value => value.BundleId == bundleId)
            .Select(value => (int?)value.Version)
            .MaxAsync(cancellationToken) ?? 0;
        dbContext.ErpBundleVersions.Add(new ErpBundleVersion
        {
            GenerationRun = execution.WorkItem.GenerationRun,
            GenerationRunId = execution.WorkItem.GenerationRunId,
            GenerationArtifactVersion = artifact,
            GenerationArtifactVersionId = artifact.Id,
            BundleId = bundleId,
            Version = nextVersion + 1,
            ContentHash = bundleContentHash,
            Summary = bundle.Summary
        });
    }

    private async Task RecordSupervisorDecisionAsync(
        AgentWorkItemExecution primary,
        GenerationEvidenceSnapshot evidenceSnapshot,
        SupervisorDecision decision,
        CancellationToken cancellationToken)
    {
        var supervisorKey = primary.Definition.SupervisorAgentKey
            ?? throw new InvalidOperationException(
                $"Agent '{primary.Definition.Key}' has no registered supervisor.");
        var definition = registry.Get(supervisorKey);
        ValidateSupervisorDecision(definition, decision);
        var workItemKey = $"{primary.WorkItem.WorkItemKey}:supervisor";
        var workItem = await dbContext.GenerationAgentWorkItems
            .Include(value => value.Attempts)
            .SingleOrDefaultAsync(
                value =>
                    value.GenerationRunId == primary.WorkItem.GenerationRunId &&
                    value.WorkItemKey == workItemKey,
                cancellationToken);
        if (workItem is null)
        {
            workItem = CreateWorkItem(
                primary.WorkItem.GenerationRun,
                primary.WorkItem.GenerationRunStage,
                definition,
                workItemKey,
                primary.Attempt.OutputArtifactHash ?? primary.Attempt.InputManifestHash,
                primary.WorkItem);
            dbContext.GenerationAgentWorkItems.Add(workItem);
            var edge = new GenerationAgentWorkItemDependency
            {
                GenerationAgentWorkItem = workItem,
                GenerationAgentWorkItemId = workItem.Id,
                DependsOnWorkItem = primary.WorkItem,
                DependsOnWorkItemId = primary.WorkItem.Id
            };
            workItem.Dependencies.Add(edge);
            dbContext.GenerationAgentWorkItemDependencies.Add(edge);
        }
        else
        {
            EnsureDefinitionMatches(workItem, definition);
        }

        var now = timeProvider.GetUtcNow();
        workItem.Status = AgentWorkItemStatuses.Completed;
        workItem.AttemptCount += 1;
        workItem.StartedAt ??= now;
        workItem.CompletedAt = now;
        workItem.Summary = Bounded(decision.Reason);
        workItem.InputManifestHash =
            primary.Attempt.OutputArtifactHash ?? primary.Attempt.InputManifestHash;
        var attempt = new GenerationAgentExecutionAttempt
        {
            GenerationAgentWorkItem = workItem,
            GenerationAgentWorkItemId = workItem.Id,
            AttemptNumber = workItem.AttemptCount,
            Status = AgentAttemptStatuses.Completed,
            WorkerId = primary.Attempt.WorkerId,
            LeaseId = primary.Attempt.LeaseId,
            AgentKey = definition.Key,
            AgentVersion = definition.Version,
            DefinitionHash = definition.DefinitionHash,
            InputManifestHash = workItem.InputManifestHash,
            InputEnvelopeJson = JsonSerializer.Serialize(
                new AgentInputEnvelope(
                    workItem.GenerationRunId,
                    workItem.GenerationRunStageId,
                    definition.Key,
                    definition.Version,
                    definition.OutputSchemaVersion,
                    workItem.InputManifestHash,
                    evidenceSnapshot.ContentSha256,
                    EvidenceReferences(evidenceSnapshot),
                    []),
                JsonOptions),
            StartedAt = now,
            CompletedAt = now
        };
        workItem.Attempts.Add(attempt);
        dbContext.GenerationAgentExecutionAttempts.Add(attempt);
        var supervisorExecution =
            new AgentWorkItemExecution(workItem, attempt, definition);
        var envelope = new AgentOutputEnvelope(
            definition.Key,
            definition.Version,
            definition.OutputSchemaVersion,
            workItem.InputManifestHash,
            JsonSerializer.SerializeToElement(decision, JsonOptions),
            EvidenceReferences(evidenceSnapshot),
            [],
            [],
            [],
            []);
        var artifact = await CreateArtifactAsync(
            supervisorExecution,
            evidenceSnapshot,
            $"{primary.Definition.OutputArtifactType}_supervisor_decision",
            definition.OutputSchemaName,
            definition.OutputSchemaVersion,
            envelope,
            decision.Reason,
            cancellationToken);
        attempt.OutputArtifactHash = artifact.ContentHash;
        GenerationRunEventFactory.Add(
            dbContext,
            primary.WorkItem.GenerationRun,
            GenerationRunEventTypes.SupervisorDecisionRecorded,
            $"Supervisor selected '{decision.Decision}' for '{primary.Definition.Key}'.",
            primary.WorkItem.GenerationRunStage.StageKey,
            primary.Attempt.AttemptNumber,
            JsonSerializer.Serialize(new
            {
                supervisorAgentKey = definition.Key,
                decision = decision.Decision,
                targetAgentKey = decision.TargetAgentKey
            }, JsonOptions));
    }

    private static AgentOutputEnvelope CreateOutputEnvelope(
        AgentWorkItemExecution execution,
        GenerationEvidenceSnapshot evidenceSnapshot,
        GenerationStageResult result)
    {
        var assumptions = result.Output is GeneratedErpBundle bundle
            ? bundle.Assumptions
            : [];
        var uncertainties = result.Output switch
        {
            GeneratedErpBundle generated => generated.OpenIssues,
            DiscoveryAssessment assessment => assessment.MissingInformation
                .Select(value => $"{value.Question} — {value.Reason}")
                .ToArray(),
            _ => []
        };
        var routes = result.Outcome == GenerationStageOutcome.WaitingForInput
            ? new[]
            {
                new AgentProposedRoute(
                    SupervisorDecisionTypes.RequestEvidence,
                    null,
                    "The typed discovery result identified missing evidence.")
            }
            : Array.Empty<AgentProposedRoute>();
        return new AgentOutputEnvelope(
            execution.Definition.Key,
            execution.Definition.Version,
            execution.Definition.OutputSchemaVersion,
            execution.WorkItem.InputManifestHash,
            JsonSerializer.SerializeToElement(result.Output, JsonOptions),
            EvidenceReferences(evidenceSnapshot),
            assumptions,
            uncertainties,
            [],
            routes);
    }

    private static AgentEvidenceReference[] EvidenceReferences(
        GenerationEvidenceSnapshot snapshot) =>
        snapshot.Items.Select(value => new AgentEvidenceReference(
            value.SourceType,
            value.SourceEntityId,
            value.ExternalId,
            value.Title,
            value.ContentSha256)).ToArray();

    private static void EnsureDefinitionMatches(
        GenerationAgentWorkItem workItem,
        AgentDefinition definition)
    {
        if (workItem.AgentKey != definition.Key ||
            workItem.AgentVersion != definition.Version ||
            workItem.DefinitionHash != definition.DefinitionHash)
        {
            throw new InvalidOperationException(
                $"Durable work item '{workItem.WorkItemKey}' does not match the active immutable agent definition.");
        }
    }

    private static void ValidateSupervisorDecision(
        AgentDefinition supervisor,
        SupervisorDecision decision)
    {
        var allowedDecisions = new HashSet<string>(StringComparer.Ordinal)
        {
            SupervisorDecisionTypes.Accept,
            SupervisorDecisionTypes.RepairSameAgent,
            SupervisorDecisionTypes.RouteToAgent,
            SupervisorDecisionTypes.RouteToPreviousStage,
            SupervisorDecisionTypes.RequestEvidence,
            SupervisorDecisionTypes.RequestInterview,
            SupervisorDecisionTypes.Fail
        };
        if (!supervisor.IsSupervisor || !allowedDecisions.Contains(decision.Decision))
            throw new InvalidOperationException(
                $"Supervisor '{supervisor.Key}' produced an invalid decision.");
        if (decision.TargetAgentKey is not null &&
            !supervisor.AllowedRouteAgentKeys.Contains(
                decision.TargetAgentKey,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Supervisor '{supervisor.Key}' cannot route to agent '{decision.TargetAgentKey}'.");
        }
        if (decision.Decision is SupervisorDecisionTypes.RepairSameAgent or
                SupervisorDecisionTypes.RouteToAgent &&
            string.IsNullOrWhiteSpace(decision.TargetAgentKey))
        {
            throw new InvalidOperationException(
                $"Supervisor decision '{decision.Decision}' requires a target agent.");
        }
    }

    private static void Release(
        GenerationAgentWorkItem workItem,
        DateTimeOffset now)
    {
        workItem.HeartbeatAt = now;
        workItem.LeaseOwner = null;
        workItem.LeaseId = null;
        workItem.LeaseExpiresAt = null;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? Bounded(string? value) =>
        value is null || value.Length <= 4_000 ? value : value[..4_000];

    private static string SafeMetadata(
        string agentKey,
        Guid workItemId,
        Guid attemptId) =>
        JsonSerializer.Serialize(new
        {
            agentKey,
            workItemId,
            attemptId
        }, JsonOptions);
}
