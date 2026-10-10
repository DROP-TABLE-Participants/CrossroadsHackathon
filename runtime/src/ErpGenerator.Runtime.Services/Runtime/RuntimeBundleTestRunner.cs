using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases.Scheduling;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed class RuntimeBundleTestRunner(
    ITenantDatabaseConnectionFactory connectionFactory,
    RuntimeSeedRunner seeds,
    RuntimeRecordStore records,
    RuntimeWorkflowStore workflows,
    RuntimeActionStore actions,
    RuntimeReportStore reports,
    TimeProvider? timeProvider = null,
    RuntimeScheduleCycle? scheduleCycle = null) : IRuntimeBundleTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimePermissionService permissions = new(connectionFactory);
    private readonly RuntimeUserStore users = new(connectionFactory);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly RuntimeScheduleCycle scheduleCycle = scheduleCycle ?? new RuntimeScheduleCycle(
        new RuntimeScheduleStore(connectionFactory, timeProvider: timeProvider ?? TimeProvider.System),
        new RuntimeScheduledWorkflowExecutor(workflows));

    internal static bool IsExactSuccessfulProducedRecordReference(
        IReadOnlyDictionary<string, RuntimeSeedRecordRef> successfulProducedRecords,
        RuntimeSeedRecordRef reference,
        string entityId) =>
        successfulProducedRecords.TryGetValue(reference.Ref, out var produced) &&
        produced == reference &&
        produced.Entity.Equals(entityId, StringComparison.Ordinal);

    public async Task<RuntimeBundleTestRunResult> RunAsync(
        ResolvedTenantEnvironment tenant,
        string bundleVersion,
        BundleSeedDocument? seed,
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        CancellationToken cancellationToken = default)
    {
        var active = await ReadActiveBundleBindingAsync(tenant, cancellationToken);
        if (active is null || !string.Equals(active.Value.Version, bundleVersion, StringComparison.Ordinal))
        {
            throw new RuntimeBundleTestException(
                $"Bundle acceptance tests require active bundle version '{bundleVersion}'.");
        }

        return await RunAsync(
            tenant,
            bundleVersion,
            active.Value.Hash,
            seed,
            tests,
            cancellationToken);
    }

    public async Task<RuntimeBundleTestRunResult> RunAsync(
        ResolvedTenantEnvironment tenant,
        string bundleVersion,
        string bundleHash,
        BundleSeedDocument? seed,
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        CancellationToken cancellationToken = default)
    {
        if (tenant.Environment != TenantEnvironmentKind.Erp)
        {
            throw new RuntimeBundleTestException("Bundle acceptance tests can run only against a staging tenant environment.");
        }

        if (string.IsNullOrWhiteSpace(bundleVersion) || string.IsNullOrWhiteSpace(bundleHash))
        {
            throw new RuntimeBundleTestException(
                "Bundle acceptance tests require an exact bundle version and bundle hash.");
        }

        BundleAcceptanceTestSuiteBinding suite;
        try
        {
            suite = BundleAcceptanceTestContractHasher.Verify(tests);
        }
        catch (BundleAcceptanceTestContractException exception)
        {
            throw new RuntimeBundleTestException(exception.Message);
        }

        var usesV3 = suite.Tests.All(contract => IsV3(contract.Document));
        var seedHash = seed is null ? string.Empty : BundleSeedContractHasher.ComputeHash(seed);
        if (usesV3 && (seed is null || suite.Tests.Any(contract =>
                !string.Equals(contract.Document.Setup.Seed, seed.Seed.Key, StringComparison.Ordinal) ||
                !string.Equals(contract.Document.Setup.SeedHash, seedHash, StringComparison.Ordinal))))
        {
            throw new RuntimeBundleTestException(
                "V3 bundle acceptance tests require the exact hash-bound seed key and contract.");
        }

        var active = await ReadActiveBundleBindingAsync(tenant, cancellationToken);
        if (active is null ||
            !string.Equals(active.Value.Version, bundleVersion, StringComparison.Ordinal) ||
            !string.Equals(active.Value.Hash, bundleHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeBundleTestException(
                "Bundle acceptance tests can run only against the exact active bundle version and hash.");
        }

        if (usesV3)
        {
            await ValidateActiveV3ActorRolesAsync(tenant, tests, cancellationToken);
            ValidateV3ActorPrincipalIds(tests, suite.SuiteHash);
        }

        var isolation = usesV3
            ? await AcquireTestDatabaseLeaseAsync(
                tenant,
                bundleVersion,
                bundleHash,
                suite.SuiteHash,
                cancellationToken)
            : null;
        var completed = false;
        try
        {

            var legacySeedRefs = new Dictionary<string, RuntimeSeedRecordRef>(StringComparer.OrdinalIgnoreCase);
            var legacyContext = new RuntimeBundleTestContext(legacySeedRefs);
            if (!usesV3 && seed is not null &&
                tests.Any(test => string.Equals(test.Setup.Seed, seed.Seed.Key, StringComparison.OrdinalIgnoreCase)))
            {
                var seedResult = await seeds.ApplySeedAsync(tenant, SystemTestUser(), seed, cancellationToken);
                foreach (var (key, value) in seedResult.Refs)
                {
                    legacySeedRefs[key] = value;
                    legacyContext.Refs[key] = value;
                }
            }

            var results = new List<RuntimeBundleTestResult>();
            foreach (var contract in suite.Tests)
            {
                var test = contract.Document;
                var isV3 = IsV3(test);
                var failures = new List<string>();
                var stepObservations = new List<RuntimeBundleTestStepObservation>();
                var assertionObservations = new List<RuntimeBundleTestAssertionObservation>();

                if (isV3)
                {
                    await CleanupTestOwnedDataAsync(isolation!, deleteTestAudit: false, cancellationToken);
                }

                try
                {
                    var context = legacyContext;
                    if (isV3)
                    {
                        var seedResult = await seeds.ApplySeedAsync(
                            tenant,
                            SystemTestUser(),
                            seed!,
                            cancellationToken);
                        ValidateV3RecordReferences(test, seedResult.Refs.Keys);
                        context = new RuntimeBundleTestContext(seedResult.Refs);
                        context.AcceptanceRunId = isolation!.RunId;
                        context.AcceptanceFenceOwnerConnection = isolation.Connection;
                    }

                    var persistedActors = isV3
                        ? await PersistV3ActorsAsync(
                            tenant,
                            isolation!,
                            test,
                            suite.SuiteHash,
                            cancellationToken)
                        : null;
                    context.ConfigureActors(test, persistedActors);
                    context.AuditEventBoundaryId = await LatestAuditEventIdAsync(tenant, cancellationToken);
                    if (isV3)
                    {
                        await ExecuteAssertionCheckpointAsync(
                            tenant,
                            context,
                            test.Assertions,
                            contract.AssertionBindings,
                            BundleAcceptanceAssertionCheckpoints.Setup,
                            failures,
                            assertionObservations,
                            cancellationToken);
                    }

                    foreach (var step in test.Steps)
                    {
                        if (failures.Count > 0)
                        {
                            break;
                        }

                        context.AuditEventBoundariesByStep[step.Id] = await LatestAuditEventIdAsync(tenant, cancellationToken);
                        if (isV3 && test.Assertions.Any(assertion =>
                                assertion.Type.Equals("notification_exists", StringComparison.OrdinalIgnoreCase) &&
                                assertion.AfterStep.Equals(step.Id, StringComparison.Ordinal)))
                        {
                            context.NotificationBoundariesByStep[step.Id] = await LatestNotificationIdAsync(tenant, cancellationToken);
                        }
                        var stepBinding = contract.StepBindings.Single(binding => binding.Id == step.Id);
                        var observation = await ExecuteObservedStepAsync(
                            tenant,
                            context,
                            step,
                            stepBinding.ContractHash,
                            cancellationToken);
                        stepObservations.Add(observation);
                        if (!string.Equals(
                                observation.Status,
                                RuntimeBundleTestStepObservationStatuses.Passed,
                                StringComparison.Ordinal))
                        {
                            failures.Add(observation.Detail);
                            break;
                        }

                        if (isV3)
                        {
                            if (test.Assertions.Any(assertion =>
                                    assertion.Type.Equals("notification_exists", StringComparison.OrdinalIgnoreCase) &&
                                    assertion.AfterStep.Equals(step.Id, StringComparison.Ordinal)))
                            {
                                context.NotificationCheckpointsByStep[step.Id] = await LatestNotificationIdAsync(tenant, cancellationToken);
                            }

                            await ExecuteAssertionCheckpointAsync(
                                tenant,
                                context,
                                test.Assertions,
                                contract.AssertionBindings,
                                step.Id,
                                failures,
                                assertionObservations,
                                cancellationToken);
                            if (failures.Count > 0)
                            {
                                break;
                            }
                        }
                    }

                    foreach (var assertion in failures.Count == 0 && !isV3 ? test.Assertions : [])
                    {
                        var assertionIndex = test.Assertions.IndexOf(assertion);
                        var before = failures.Count;
                        await ExecuteAssertionAsync(tenant, context, assertion, failures, cancellationToken);
                        assertionObservations.Add(BuildAssertionObservation(
                            context,
                            assertion,
                            contract.AssertionBindings[assertionIndex].ContractHash,
                            failures.Count == before,
                            failures.Count == before ? string.Empty : failures[^1]));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(isV3
                        ? RuntimeBundleTestEvidenceFailureCodes.TestExecutionError
                        : ex.Message);
                }
                finally
                {
                    if (isV3)
                    {
                        await CleanupTestOwnedDataAsync(
                            isolation!,
                            deleteTestAudit: false,
                            CancellationToken.None);
                    }
                }

                results.Add(new RuntimeBundleTestResult(
                    test.Test.Key,
                    test.Test.Label,
                    test.SourcePath,
                    failures.Count == 0 ? "passed" : "failed",
                    failures,
                    contract.ContractHash,
                    contract.Origin,
                    contract.SuiteIndex)
                {
                    StepObservations = stepObservations,
                    AssertionObservations = isV3
                        ? assertionObservations
                            .OrderBy(observation => contract.AssertionBindings
                                .Single(binding => binding.Id == observation.AssertionId).Index)
                            .ToArray()
                        : assertionObservations
                });
            }

            if (usesV3)
            {
                await CleanupTestOwnedDataAsync(isolation!, deleteTestAudit: true, cancellationToken);
                if (seed is { Records.Count: > 0 })
                {
                    await seeds.ApplySeedAsync(
                        tenant,
                        SystemTestUser(),
                        seed,
                        cancellationToken);
                }
            }

            var summary = new RuntimeBundleTestRunResult(
                bundleVersion,
                results.Count,
                results.Count(result => result.Status.Equals("passed", StringComparison.OrdinalIgnoreCase)),
                results.Count(result => result.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)),
                results.Count > 0 && results.All(result => result.Status.Equals("passed", StringComparison.OrdinalIgnoreCase)) ? "passed" : "failed",
                results,
                bundleHash,
                suite.SuiteHash)
            {
                SeedHash = seedHash
            };

            await records.RecordAuditEventAsync(
                tenant,
                SystemTestUser(),
                "bundle_tests.completed",
                $"Bundle acceptance tests {summary.Status}.",
                new Dictionary<string, object?>
                {
                    ["bundleVersion"] = bundleVersion,
                    ["bundleHash"] = bundleHash,
                    ["testSuiteHash"] = suite.SuiteHash,
                    ["seedHash"] = seedHash,
                    ["total"] = summary.Total,
                    ["passed"] = summary.Passed,
                    ["failed"] = summary.Failed
                },
                cancellationToken);
            await StoreResultAsync(tenant, summary, cancellationToken);

            completed = true;
            return summary;
        }
        finally
        {
            try
            {
                if (!completed && isolation is not null)
                {
                    await CleanupTestOwnedDataAsync(
                        isolation,
                        deleteTestAudit: true,
                        CancellationToken.None);
                    if (seed is { Records.Count: > 0 })
                    {
                        await seeds.ApplySeedAsync(
                            tenant,
                            SystemTestUser(),
                            seed,
                            CancellationToken.None);
                    }
                }
            }
            finally
            {
                if (isolation is not null)
                {
                    await ReleaseTestDatabaseLeaseAsync(isolation, CancellationToken.None);
                }
            }
        }
    }

    public async Task<bool> HasPassingResultAsync(
        ResolvedTenantEnvironment tenant,
        string bundleVersion,
        string bundleHash,
        string testSuiteHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bundleVersion) ||
            string.IsNullOrWhiteSpace(bundleHash) ||
            testSuiteHash is not { Length: 64 })
        {
            return false;
        }

        await using var connection = await OpenProvisioningConnectionAsync(tenant, cancellationToken);
        await EnsureTestResultsTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select status
            from _runtime.test_results
            where bundle_version = @bundleVersion
              and bundle_hash = @bundleHash
              and test_suite_hash = @testSuiteHash
            order by id desc
            limit 1
            """;
        command.Parameters.AddWithValue("bundleVersion", bundleVersion);
        command.Parameters.AddWithValue("bundleHash", bundleHash);
        command.Parameters.AddWithValue("testSuiteHash", testSuiteHash);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RuntimeBundleTestStepObservation> ExecuteObservedStepAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestContext context,
        BundleAcceptanceStep step,
        string contractItemHash,
        CancellationToken cancellationToken)
    {
        var actor = context.ResolveActor(step);
        if (RuntimeBundleTestOutcomeClassifier.TryFindUnsupportedExpectedOutcome(
                step,
                out var unsupported,
                supportsExactWorkflowTaskPermissionDenials: context.IsV3))
        {
            return BindStepObservation(RuntimeBundleTestOutcomeClassifier.Observe(
                step,
                unsupported,
                RuntimeBundleTestStepObservationStatuses.Unsupported),
                context,
                step,
                actor,
                contractItemHash);
        }

        RuntimeBundleTestStepActualOutcome actual;
        try
        {
            actual = await ExecuteStepAsync(tenant, context, step, actor.User, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            actual = RuntimeBundleTestOutcomeClassifier.FromException(step, exception);
        }

        return BindStepObservation(
            RuntimeBundleTestOutcomeClassifier.Observe(step, actual),
            context,
            step,
            actor,
            contractItemHash);
    }

    private async Task<RuntimeBundleTestStepActualOutcome> ExecuteStepAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestContext context,
        BundleAcceptanceStep step,
        RuntimeUserContext user,
        CancellationToken cancellationToken)
    {
        var actual = RuntimeBundleTestStepActualOutcome.Success;
        switch (step.Type.Trim().ToLowerInvariant())
        {
            case BundleAcceptanceStepTypes.ReferenceSeedRecord:
                {
                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    context.ReferenceSeedRecord(step.Id, step.Record?.Ref ?? string.Empty, entity.Id);
                    break;
                }
            case "create_record":
                {
                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    await EnsureAllowedAsync(
                        tenant,
                        user,
                        "create",
                        entity,
                        step.Id,
                        BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate,
                        step.Entity,
                        cancellationToken);
                    await EnsureWritableFieldsAllowedAsync(
                        tenant,
                        user,
                        entity,
                        step.Values.Keys,
                        step.Id,
                        cancellationToken);
                    var values = RuntimeSeedRunner.ResolveRecordValues(
                        step.Id,
                        entity,
                        context.ResolveContractValues(step.Values),
                        context.Refs);
                    var created = await records.CreateRecordAsync(tenant, user, entity.Key, values, cancellationToken);
                    context.RecordSuccessfulProducer(new RuntimeSeedRecordRef(
                        step.Id,
                        entity.Id,
                        entity.Key,
                        Convert.ToInt64(created["id"], CultureInfo.InvariantCulture)));
                    break;
                }
            case "update_record":
                {
                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    await EnsureAllowedAsync(
                        tenant,
                        user,
                        "update",
                        entity,
                        step.Id,
                        BundleAcceptanceExpectedOutcomeCodes.PermissionEntityUpdate,
                        step.Entity,
                        cancellationToken);
                    await EnsureWritableFieldsAllowedAsync(
                        tenant,
                        user,
                        entity,
                        step.Set.Keys.Where(field =>
                            !IsNamedActionLifecycleBoundaryProbe(step, entity, field)),
                        step.Id,
                        cancellationToken);
                    var reference = ResolveRecordReference(context, step.Record, entity.Id);
                    var recordId = reference.Id;
                    var values = RuntimeSeedRunner.ResolveRecordUpdateValues(
                        step.Id,
                        entity,
                        context.ResolveContractValues(step.Set),
                        context.Refs);
                    var updated = await records.UpdateRecordAsync(tenant, user, entity.Key, recordId, values, cancellationToken);
                    if (updated is null)
                    {
                        if (context.IsV3 &&
                            context.IsExactSuccessfulProducedRecordReference(reference, entity.Id))
                        {
                            return RuntimeBundleTestOutcomeClassifier.RecordUpdatePermissionDenied(step);
                        }

                        throw new RuntimeBundleTestException($"Step '{step.Id}' could not update {entity.Id} record {recordId}.");
                    }

                    break;
                }
            case BundleAcceptanceStepTypes.BindActorRecord:
                {
                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    var reference = ResolveRecordReference(context, step.Record, entity.Id);
                    if (!string.Equals(step.Entity, entity.Id, StringComparison.Ordinal) ||
                        !string.Equals(reference.Entity, entity.Id, StringComparison.Ordinal))
                    {
                        throw new RuntimeBundleTestException(
                            $"Acceptance step '{step.Id}' must bind the exact canonical entity produced by its record ref.");
                    }

                    if (context.AcceptanceRunId is not { } runId)
                    {
                        throw new RuntimeBundleTestException(
                            $"Acceptance step '{step.Id}' requires an active V3 database lease.");
                    }

                    await users.BindOwnedAcceptanceActorToRecordAsync(
                        tenant,
                        runId,
                        user.UserId,
                        entity.Id,
                        reference.Id,
                        cancellationToken);
                    break;
                }
            case BundleAcceptanceStepTypes.RunDueSchedule:
                {
                    if (!context.IsV3 ||
                        context.AcceptanceRunId is not { } runId ||
                        context.AcceptanceFenceOwnerConnection is not { } fenceOwner)
                    {
                        throw new RuntimeBundleTestException(
                            $"Acceptance run_due_schedule step '{step.Id}' requires the exact active V3 database lease and fence owner.");
                    }

                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    var reference = ResolveRecordReference(context, step.Record, entity.Id);
                    var authority = await ResolveExactActiveScheduleAuthorityAsync(
                        fenceOwner,
                        step,
                        entity.Id,
                        cancellationToken);

                    // Refreshing under the already-owned acceptance fence is safe and
                    // materializes the exact target without claiming or executing it.
                    await scheduleCycle.RefreshWithOwnedAcceptanceFenceAsync(
                        tenant,
                        fenceOwner,
                        cancellationToken);
                    var prepared = FindExactScheduleJob(
                        await scheduleCycle.ListJobsAsync(tenant, cancellationToken),
                        step,
                        reference.Id);
                    if (!prepared.ScheduleDefinitionHash.Equals(authority.DefinitionHash, StringComparison.Ordinal))
                    {
                        throw new RuntimeBundleTestException(
                            $"Acceptance run_due_schedule step '{step.Id}' target job is not bound to the exact active schedule definition.");
                    }
                    var observedAt = timeProvider.GetUtcNow();
                    ValidateClosedScheduleTime(step, prepared, observedAt);

                    var cycle = await scheduleCycle.RunOnceWithOwnedAcceptanceFenceAsync(
                        tenant,
                        $"acceptance_{runId:N}",
                        fenceOwner,
                        cancellationToken);
                    var completed = FindExactScheduleJob(
                        await scheduleCycle.ListJobsAsync(tenant, cancellationToken),
                        step,
                        reference.Id);
                    if (completed.Id != prepared.Id ||
                        !completed.ScheduleDefinitionHash.Equals(prepared.ScheduleDefinitionHash, StringComparison.Ordinal) ||
                        !completed.ScheduleDefinitionHash.Equals(authority.DefinitionHash, StringComparison.Ordinal))
                    {
                        throw new RuntimeBundleTestException(
                            $"Acceptance run_due_schedule step '{step.Id}' lost its exact one-shot job identity.");
                    }

                    ValidateScheduleOperationResult(step, completed);
                    if (completed.WorkflowInstanceId is { } workflowInstanceId)
                    {
                        context.RecordScheduledWorkflowStart(
                            step.Id,
                            await LoadExactScheduledWorkflowStartAsync(
                                tenant,
                                step,
                                reference.Id,
                                authority,
                                completed,
                                workflowInstanceId,
                                cancellationToken));
                    }
                    context.RecordScheduleRun(new RuntimeBundleTestScheduleRun(
                        step.Id,
                        step.Schedule,
                        step.ScheduleTime,
                        entity.Id,
                        reference.Ref,
                        reference.Id,
                        authority.WorkflowId,
                        observedAt,
                        completed,
                        cycle));
                    break;
                }
            case "run_action":
                {
                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    var recordId = ResolveRecordId(context, step.Record, entity.Id);
                    var actionDefinition = await actions.GetActionAsync(
                        tenant,
                        entity.Key,
                        step.Action,
                        cancellationToken);
                    var exactProducedEntity = string.Empty;
                    var hasExactProducedRecord = actionDefinition is not null &&
                                                 BundleAcceptanceRecordProducerContract.TryResolveActionProducedEntity(
                                                     actionDefinition,
                                                     out exactProducedEntity);
                    var result = await actions.ExecuteActionAsync(tenant, user, entity.Key, recordId, step.Action, cancellationToken);
                    if (hasExactProducedRecord)
                    {
                        if (result.Created is null ||
                            !result.Created.EntityId.Equals(exactProducedEntity, StringComparison.Ordinal))
                        {
                            throw new RuntimeBundleTestException(
                                $"Acceptance run_action step '{step.Id}' did not return its one exact declared created record.");
                        }

                        context.RecordSuccessfulProducer(new RuntimeSeedRecordRef(
                            step.Id,
                            result.Created.EntityId,
                            result.Created.Entity,
                            result.Created.Id));
                    }

                    break;
                }
            case "start_workflow_action":
                {
                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    var matchingWorkflows = (await workflows.ListWorkflowsAsync(tenant, cancellationToken))
                        .Where(workflow =>
                            workflow.Entity.Equals(entity.Id, StringComparison.Ordinal) &&
                            workflow.Trigger?.Action.Equals(step.Action, StringComparison.Ordinal) == true)
                        .Take(2)
                        .ToArray();
                    if (matchingWorkflows.Length != 1)
                    {
                        throw new RuntimeBundleTestException(
                            $"Acceptance step '{step.Id}' action '{step.Action}' must resolve to one exact workflow for entity '{entity.Id}'.");
                    }

                    var canStart = await permissions.IsAllowedAsync(
                        tenant,
                        user,
                        "start_workflow",
                        entity,
                        cancellationToken);
                    if (!canStart)
                    {
                        throw new RuntimeBundleTestPermissionDeniedException(
                            BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowStart,
                            matchingWorkflows[0].Id,
                            $"Acceptance step '{step.Id}' role '{user.Role}' is not allowed to start workflow '{matchingWorkflows[0].Id}'.");
                    }

                    var recordId = ResolveRecordId(context, step.Record, entity.Id);
                    RuntimeWorkflowStartResult result;
                    try
                    {
                        result = await workflows.StartActionAsync(
                            tenant,
                            user,
                            entity.Key,
                            recordId,
                            step.Action,
                            cancellationToken);
                    }
                    catch (RuntimeWorkflowRecordStartPermissionException denial)
                    {
                        throw new RuntimeBundleTestPermissionDeniedException(
                            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordStartWorkflow,
                            entity.Id,
                            denial.Message);
                    }

                    context.WorkflowStarts[step.Id] = result;
                    break;
                }
            case "approve_task":
            case "reject_task":
                {
                    var entity = await ResolveEntityAsync(tenant, step.Entity, cancellationToken);
                    var recordId = ResolveRecordId(context, step.Record, entity.Id);
                    RuntimeWorkflowStartResult? exactDeniedTaskBinding = null;
                    RuntimeWorkflowTask task;
                    if (ExpectsWorkflowTaskPermissionDenial(step))
                    {
                        exactDeniedTaskBinding = ResolveExactReferencedPendingTaskBinding(
                            context,
                            step,
                            entity,
                            recordId);
                        task = exactDeniedTaskBinding.Task!;
                    }
                    else
                    {
                        task = await FindPendingTaskAsync(
                            tenant,
                            user,
                            context,
                            step,
                            entity,
                            recordId,
                            cancellationToken);
                    }

                    if (step.Type.Equals("approve_task", StringComparison.OrdinalIgnoreCase))
                    {
                        context.WorkflowTasks[step.Id] = exactDeniedTaskBinding is null
                            ? await workflows.ApproveTaskAsync(
                                tenant,
                                user,
                                task.Id,
                                cancellationToken: cancellationToken)
                            : await workflows.ApproveExactPendingTaskForAcceptanceAsync(
                                tenant,
                                user,
                                exactDeniedTaskBinding,
                                cancellationToken: cancellationToken);
                    }
                    else
                    {
                        var result = exactDeniedTaskBinding is null
                            ? await workflows.RejectTaskAsync(
                                tenant,
                                user,
                                task.Id,
                                "Bundle acceptance test rejection.",
                                cancellationToken)
                            : await workflows.RejectExactPendingTaskForAcceptanceAsync(
                                tenant,
                                user,
                                exactDeniedTaskBinding,
                                "Bundle acceptance test rejection.",
                                cancellationToken);
                        context.WorkflowTasks[step.Id] = result;
                        actual = RuntimeBundleTestOutcomeClassifier.FromWorkflowRejectionResult(
                            step,
                            result.Task.Status,
                            result.Instance.Status,
                            result.Task.StepId);
                    }

                    break;
                }
            case "mark_notification_read":
                await workflows.MarkAllNotificationsReadAsync(tenant, user, cancellationToken);
                break;
            case "call_report":
                context.Reports[step.Id] = await ExecuteReportAsync(
                    tenant,
                    step.Report,
                    user,
                    cancellationToken,
                    classifyPermissionDenial: true);
                break;
            default:
                throw new RuntimeBundleTestException($"Unsupported acceptance test step type '{step.Type}'.");
        }

        return actual;
    }

    private static async Task<RuntimeBundleTestScheduleAuthority> ResolveExactActiveScheduleAuthorityAsync(
        NpgsqlConnection connection,
        BundleAcceptanceStep step,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select schedules.entity_id, schedules.workflow_id, schedules.definition_hash
            from _runtime.schedule_definitions schedules
            join _runtime.active_bundle active
              on active.bundle_id = schedules.bundle_id
             and active.bundle_version = schedules.bundle_version
             and active.bundle_hash = schedules.bundle_hash
            where schedules.id = @scheduleId
            """;
        command.Parameters.AddWithValue("scheduleId", step.Schedule);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance run_due_schedule step '{step.Id}' schedule '{step.Schedule}' is not one exact active compiled schedule.");
        }

        var persistedEntity = reader.GetString(0);
        var workflowId = reader.GetString(1);
        var definitionHash = reader.GetString(2);
        if (await reader.ReadAsync(cancellationToken) ||
            !persistedEntity.Equals(entityId, StringComparison.Ordinal) ||
            !step.Entity.Equals(entityId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(workflowId) ||
            definitionHash.Length != 64)
        {
            throw new RuntimeBundleTestException(
                $"Acceptance run_due_schedule step '{step.Id}' schedule authority does not match its exact entity and active lineage.");
        }

        return new RuntimeBundleTestScheduleAuthority(workflowId, definitionHash);
    }

    private static RuntimeScheduledJob FindExactScheduleJob(
        IReadOnlyList<RuntimeScheduledJob> jobs,
        BundleAcceptanceStep step,
        long recordId)
    {
        var matches = jobs.Where(job =>
                job.ScheduleId.Equals(step.Schedule, StringComparison.Ordinal) &&
                job.EntityId.Equals(step.Entity, StringComparison.Ordinal) &&
                job.RecordId == recordId)
            .Take(2)
            .ToArray();
        if (matches is not [{ } exact])
        {
            throw new RuntimeBundleTestException(
                $"Acceptance run_due_schedule step '{step.Id}' did not resolve one exact eligible schedule job for its record ref.");
        }

        return exact;
    }

    private static void ValidateClosedScheduleTime(
        BundleAcceptanceStep step,
        RuntimeScheduledJob job,
        DateTimeOffset observedAt)
    {
        var valid = step.ScheduleTime switch
        {
            BundleAcceptanceScheduleTimes.BeforeDue => observedAt < job.DueAt,
            BundleAcceptanceScheduleTimes.Due => observedAt >= job.DueAt,
            _ => false
        };
        if (!valid)
        {
            throw new RuntimeBundleTestException(
                $"Acceptance run_due_schedule step '{step.Id}' schedule_time '{step.ScheduleTime}' does not match the exact target due boundary.");
        }
    }

    private static void ValidateScheduleOperationResult(
        BundleAcceptanceStep step,
        RuntimeScheduledJob job)
    {
        var valid = step.ScheduleTime switch
        {
            BundleAcceptanceScheduleTimes.BeforeDue =>
                job.Status.Equals(RuntimeScheduleJobStatuses.Ready, StringComparison.Ordinal) &&
                job.WorkflowInstanceId is null,
            BundleAcceptanceScheduleTimes.Due =>
                job.Status.Equals(RuntimeScheduleJobStatuses.Completed, StringComparison.Ordinal) &&
                job.WorkflowInstanceId is > 0,
            _ => false
        };
        if (!valid)
        {
            throw new RuntimeBundleTestException(
                $"Acceptance run_due_schedule step '{step.Id}' did not produce its exact closed scheduler result.");
        }
    }

    private async Task<RuntimeWorkflowStartResult> LoadExactScheduledWorkflowStartAsync(
        ResolvedTenantEnvironment tenant,
        BundleAcceptanceStep step,
        long recordId,
        RuntimeBundleTestScheduleAuthority authority,
        RuntimeScheduledJob job,
        long workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var detail = await workflows.GetInstanceAsync(
            tenant,
            SystemTestUser(),
            workflowInstanceId,
            cancellationToken);
        var instance = detail?.Instance;
        if (instance is null ||
            instance.Id != workflowInstanceId ||
            !instance.WorkflowId.Equals(authority.WorkflowId, StringComparison.Ordinal) ||
            !instance.EntityId.Equals(step.Entity, StringComparison.Ordinal) ||
            instance.RecordId != recordId ||
            instance.StartedBy is not null ||
            !ScheduledInstanceMetadataMatches(instance.Metadata, step, job))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance run_due_schedule step '{step.Id}' did not bind one exact scheduler-owned workflow instance.");
        }

        var pending = detail!.Tasks.Where(task =>
                task.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        RuntimeWorkflowTask? task = null;
        if (instance.Status.Equals("waiting", StringComparison.OrdinalIgnoreCase))
        {
            if (pending is not [{ } exact] ||
                exact.InstanceId != instance.Id ||
                !exact.WorkflowId.Equals(instance.WorkflowId, StringComparison.Ordinal) ||
                !exact.EntityId.Equals(instance.EntityId, StringComparison.Ordinal) ||
                exact.RecordId != instance.RecordId ||
                !string.Equals(exact.StepId, instance.CurrentStepId, StringComparison.Ordinal))
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance run_due_schedule step '{step.Id}' did not bind one exact pending scheduled task.");
            }

            task = exact;
        }
        else if (!instance.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) || pending.Length != 0)
        {
            throw new RuntimeBundleTestException(
                $"Acceptance run_due_schedule step '{step.Id}' produced an unsupported workflow handoff state.");
        }

        return new RuntimeWorkflowStartResult(instance, task);
    }

    private static bool ScheduledInstanceMetadataMatches(
        JsonElement metadata,
        BundleAcceptanceStep step,
        RuntimeScheduledJob job) =>
        metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty("triggerKind", out var triggerKind) &&
        triggerKind.ValueKind == JsonValueKind.String &&
        triggerKind.GetString() == "schedule" &&
        metadata.TryGetProperty("triggerKey", out var triggerKey) &&
        triggerKey.ValueKind == JsonValueKind.String &&
        triggerKey.GetString() == step.Schedule &&
        metadata.TryGetProperty("schedulerJobId", out var schedulerJobId) &&
        schedulerJobId.TryGetInt64(out var persistedJobId) &&
        persistedJobId == job.Id &&
        metadata.TryGetProperty("scheduleDefinitionHash", out var definitionHash) &&
        definitionHash.ValueKind == JsonValueKind.String &&
        definitionHash.GetString() == job.ScheduleDefinitionHash;

    private static RuntimeBundleTestStepObservation BindStepObservation(
        RuntimeBundleTestStepObservation observation,
        RuntimeBundleTestContext context,
        BundleAcceptanceStep step,
        RuntimeBundleTestActorExecution actor,
        string contractItemHash)
    {
        context.Refs.TryGetValue(step.Record?.Ref ?? string.Empty, out var subject);
        context.Refs.TryGetValue(step.Id, out var produced);
        context.WorkflowStarts.TryGetValue(step.Id, out var started);
        context.WorkflowTasks.TryGetValue(step.Id, out var decided);
        context.ScheduleRuns.TryGetValue(step.Id, out var scheduleRun);
        var referencedStart = SelectReferencedWorkflowStartForObservation(
            step,
            context.WorkflowStarts);

        var exactTask = started?.Task ?? decided?.Task ?? referencedStart?.Task;
        return observation with
        {
            Detail = context.IsV3 ? StableV3StepDetail(observation) : observation.Detail,
            ContractItemHash = contractItemHash,
            ActorId = actor.ActorId,
            ActorRoles = actor.User.Roles.ToArray(),
            PrincipalId = actor.User.UserId,
            Entity = step.Entity,
            RecordRef = step.Record?.Ref ?? string.Empty,
            RecordId = subject?.Id,
            Action = step.Action,
            WorkflowRef = step.WorkflowRef,
            WorkflowInstanceId = started?.Instance.Id ?? decided?.Instance.Id ?? referencedStart?.Instance.Id ??
                scheduleRun?.Job.WorkflowInstanceId,
            Workflow = started?.Instance.WorkflowId ?? decided?.Instance.WorkflowId ??
                referencedStart?.Instance.WorkflowId ?? scheduleRun?.WorkflowId ?? string.Empty,
            TaskId = started?.Task?.Id ?? decided?.Task.Id ?? referencedStart?.Task?.Id,
            WorkflowStep = started?.Task?.StepId ?? decided?.Task.StepId ??
                referencedStart?.Task?.StepId ?? string.Empty,
            TaskAssignedUserId = exactTask?.AssignedUserId,
            TaskAssignedRole = exactTask?.AssignedRole ?? string.Empty,
            Report = step.Report,
            Schedule = step.Schedule,
            ScheduleTime = step.ScheduleTime,
            ScheduleObservedAt = scheduleRun?.ObservedAt,
            ScheduleJobId = scheduleRun?.Job.Id,
            ScheduleJobStatus = scheduleRun?.Job.Status ?? string.Empty,
            ScheduleDueAt = scheduleRun?.Job.DueAt,
            ScheduleCycleClaimed = scheduleRun?.Cycle.Claimed,
            ScheduleCycleCompleted = scheduleRun?.Cycle.Completed,
            ProducedRecordRef = produced?.Ref ?? string.Empty,
            ProducedEntity = produced?.Entity ?? string.Empty,
            ProducedRecordId = produced?.Id
        };
    }

    private static string StableV3StepDetail(RuntimeBundleTestStepObservation observation)
    {
        if (observation.Status.Equals(RuntimeBundleTestStepObservationStatuses.Passed, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var code = observation.Status.Equals(RuntimeBundleTestStepObservationStatuses.ExecutionError, StringComparison.Ordinal) ||
                   observation.Status.Equals(RuntimeBundleTestStepObservationStatuses.Unsupported, StringComparison.Ordinal)
            ? RuntimeBundleTestEvidenceFailureCodes.StepExecutionError
            : RuntimeBundleTestEvidenceFailureCodes.StepFailed;
        return $"{code}:{observation.StepId}";
    }

    private async Task ExecuteAssertionCheckpointAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestContext context,
        IReadOnlyList<BundleAcceptanceAssertion> assertions,
        IReadOnlyList<BundleAcceptanceContractItemBinding> bindings,
        string checkpoint,
        List<string> failures,
        List<RuntimeBundleTestAssertionObservation> observations,
        CancellationToken cancellationToken)
    {
        foreach (var (assertion, index) in assertions.Select((assertion, index) => (assertion, index))
                     .Where(item => item.assertion.AfterStep.Equals(checkpoint, StringComparison.Ordinal)))
        {
            var before = failures.Count;
            try
            {
                await ExecuteAssertionAsync(tenant, context, assertion, failures, cancellationToken);
                var passed = failures.Count == before;
                var detail = passed
                    ? string.Empty
                    : StableV3AssertionDetail(assertion, executionError: false);
                if (!passed)
                {
                    failures.RemoveRange(before, failures.Count - before);
                    failures.Add(detail);
                }

                observations.Add(BuildAssertionObservation(
                    context,
                    assertion,
                    bindings[index].ContractHash,
                    passed,
                    detail));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var detail = context.IsV3
                    ? StableV3AssertionDetail(assertion, executionError: true)
                    : exception.Message;
                failures.Add(detail);
                observations.Add(BuildAssertionObservation(
                    context,
                    assertion,
                    bindings[index].ContractHash,
                    passed: false,
                    detail,
                    executionError: true));
            }
        }
    }

    private static string StableV3AssertionDetail(
        BundleAcceptanceAssertion assertion,
        bool executionError) =>
        $"{(executionError
            ? RuntimeBundleTestEvidenceFailureCodes.AssertionExecutionError
            : RuntimeBundleTestEvidenceFailureCodes.AssertionFailed)}:{assertion.Id}";

    private static RuntimeBundleTestAssertionObservation BuildAssertionObservation(
        RuntimeBundleTestContext context,
        BundleAcceptanceAssertion assertion,
        string contractItemHash,
        bool passed,
        string detail,
        bool executionError = false)
    {
        context.Refs.TryGetValue(assertion.Record?.Ref ?? string.Empty, out var record);
        context.WorkflowStarts.TryGetValue(assertion.WorkflowRef, out var started);
        context.Reports.TryGetValue(assertion.ReportRef, out var report);
        context.StepActors.TryGetValue(assertion.ReportRef, out var reportActor);
        context.AssertionEvidence.TryGetValue(assertion.Id, out var evidence);
        var fallbackExpected = assertion.Value ??
            (string.IsNullOrWhiteSpace(assertion.ValueRef) ? null : assertion.ValueRef);
        var hasExpectedValue = evidence?.HasExpectedValue == true || evidence is null && fallbackExpected is not null;
        var hasActualValue = evidence?.HasActualValue == true;
        var expectedValue = evidence?.ExpectedValue ?? fallbackExpected;
        var actualValue = evidence?.ActualValue;
        var expectedDigest = context.IsV3 && hasExpectedValue
            ? CreateEvidenceDigest(evidence?.ExpectedValueKind, expectedValue)
            : null;
        var actualDigest = context.IsV3 && hasActualValue
            ? CreateEvidenceDigest(evidence?.ActualValueKind, actualValue)
            : null;
        var reportMetricValues = evidence?.ReportMetricValues ?? [];
        return new RuntimeBundleTestAssertionObservation(
            assertion.Id,
            assertion.Type,
            assertion.AfterStep,
            contractItemHash,
            executionError
                ? RuntimeBundleTestAssertionObservationStatuses.ExecutionError
                : passed
                    ? RuntimeBundleTestAssertionObservationStatuses.Passed
                    : RuntimeBundleTestAssertionObservationStatuses.Failed,
            detail)
        {
            Entity = assertion.Entity,
            RecordRef = assertion.Record?.Ref ?? string.Empty,
            RecordId = record?.Id,
            Field = assertion.Field,
            ExpectedValue = context.IsV3 ? null : expectedValue,
            ActualValue = context.IsV3 ? null : actualValue,
            ExpectedValueDigestSchema = expectedDigest?.Schema ?? string.Empty,
            ExpectedValueKind = expectedDigest?.Kind ?? string.Empty,
            ExpectedValueDigest = expectedDigest?.Digest ?? string.Empty,
            ActualValueDigestSchema = actualDigest?.Schema ?? string.Empty,
            ActualValueKind = actualDigest?.Kind ?? string.Empty,
            ActualValueDigest = actualDigest?.Digest ?? string.Empty,
            WorkflowRef = assertion.WorkflowRef,
            WorkflowInstanceId = evidence?.WorkflowInstanceId ?? started?.Instance.Id,
            Workflow = assertion.Workflow,
            WorkflowStatus = evidence?.WorkflowStatus ?? string.Empty,
            TaskIds = evidence?.TaskIds ?? [],
            TaskSteps = evidence?.TaskSteps ?? [],
            TaskStatuses = evidence?.TaskStatuses ?? [],
            ReportRef = assertion.ReportRef,
            Report = assertion.Report,
            Metric = assertion.Metric,
            Widget = assertion.Widget,
            DashboardWidgetReport = evidence?.DashboardWidgetReport ?? string.Empty,
            DashboardWidgetMetric = evidence?.DashboardWidgetMetric ?? string.Empty,
            RowCount = evidence?.RowCount ?? report?.Rows.Count,
            ReportMetricValues = context.IsV3 ? [] : reportMetricValues,
            ReportMetricDigests = context.IsV3
                ? reportMetricValues
                    .Select(value => CreateEvidenceDigest(evidence?.ReportMetricValueKind, value))
                    .ToArray()
                : [],
            ReportOutputHash = evidence?.ReportOutputHash ??
                (report is null ? string.Empty : ComputeReportOutputHash(report)),
            ReportTruncated = evidence?.ReportTruncated ?? report?.IsTruncated ?? false,
            ReportAppliedLimit = evidence?.ReportAppliedLimit ?? report?.AppliedLimit,
            AuditEventIds = evidence?.AuditEventIds ?? [],
            AuditStep = assertion.AuditStep,
            NotificationIds = evidence?.NotificationIds ?? [],
            Notifications = evidence?.Notifications ?? [],
            ActorId = reportActor?.ActorId ?? string.Empty,
            PrincipalId = reportActor?.User.UserId
        };
    }

    private static BundleAcceptanceEvidenceDigest CreateEvidenceDigest(string? explicitKind, object? value) =>
        string.IsNullOrWhiteSpace(explicitKind)
            ? BundleAcceptanceEvidenceDigestContract.CreateInferred(value)
            : BundleAcceptanceEvidenceDigestContract.Create(explicitKind, value);

    private static string EvidenceValueKindForField(
        RuntimeFieldDefinition field,
        BundleAcceptanceAssertion assertion)
    {
        if (!string.IsNullOrWhiteSpace(assertion.ValueRef))
        {
            return BundleAcceptanceEvidenceValueKinds.RecordRef;
        }

        if (assertion.Value is string text && BundleAcceptanceActorPrincipalToken.IsCandidate(text))
        {
            return BundleAcceptanceEvidenceValueKinds.ActorPrincipal;
        }

        return field.FieldType.Trim().ToLowerInvariant() switch
        {
            "string" or "text" or "enum" => BundleAcceptanceEvidenceValueKinds.String,
            "integer" or "relation" => BundleAcceptanceEvidenceValueKinds.Integer,
            "number" or "decimal" or "money" => BundleAcceptanceEvidenceValueKinds.Decimal,
            "boolean" => BundleAcceptanceEvidenceValueKinds.Boolean,
            "date" => BundleAcceptanceEvidenceValueKinds.Date,
            "datetime" => BundleAcceptanceEvidenceValueKinds.DateTime,
            _ => BundleAcceptanceEvidenceValueKinds.Json
        };
    }

    private static string EvidenceValueKindForReportColumn(
        RuntimeReportColumn? column,
        object? fallbackValue)
    {
        if (column?.Kind.Equals(RuntimeReportColumnKinds.Identity, StringComparison.Ordinal) == true)
        {
            return BundleAcceptanceEvidenceValueKinds.RecordRef;
        }

        if (string.Equals(
                column?.EvidenceValueKind,
                BundleAcceptanceEvidenceValueKinds.ActorPrincipal,
                StringComparison.Ordinal))
        {
            return BundleAcceptanceEvidenceValueKinds.ActorPrincipal;
        }

        var valueType = string.IsNullOrWhiteSpace(column?.ValueType)
            ? column?.Type
            : column.ValueType;
        return valueType?.Trim().ToLowerInvariant() switch
        {
            "string" or "text" or "enum" => BundleAcceptanceEvidenceValueKinds.String,
            "integer" => BundleAcceptanceEvidenceValueKinds.Integer,
            "relation" => BundleAcceptanceEvidenceValueKinds.RecordRef,
            "number" or "decimal" or "money" => BundleAcceptanceEvidenceValueKinds.Decimal,
            "boolean" => BundleAcceptanceEvidenceValueKinds.Boolean,
            "date" => BundleAcceptanceEvidenceValueKinds.Date,
            "datetime" => BundleAcceptanceEvidenceValueKinds.DateTime,
            _ => BundleAcceptanceEvidenceDigestContract.InferKind(fallbackValue)
        };
    }

    private static string ExpectedEvidenceValueKindForReportColumn(
        RuntimeReportColumn? column,
        BundleAcceptanceAssertion assertion,
        object? fallbackValue) =>
        !string.IsNullOrWhiteSpace(assertion.ValueRef)
            ? BundleAcceptanceEvidenceValueKinds.RecordRef
            : IsActorPrincipalEvidenceValue(assertion.Value)
                ? BundleAcceptanceEvidenceValueKinds.ActorPrincipal
                : EvidenceValueKindForReportColumn(column, fallbackValue);

    private static void ValidateReportMetricValueRef(
        RuntimeBundleTestContext context,
        BundleAcceptanceAssertion assertion,
        RuntimeReportColumn? column)
    {
        if (!context.IsV3 || string.IsNullOrWhiteSpace(assertion.ValueRef))
        {
            return;
        }

        if (!context.Refs.TryGetValue(assertion.ValueRef, out var reference) ||
            column is not
            {
                Kind: RuntimeReportColumnKinds.Identity,
                IdentityEntityId: not null
            } ||
            !column.IdentityEntityId.Equals(reference.Entity, StringComparison.Ordinal))
        {
            throw new RuntimeBundleTestException(
                $"Assertion '{assertion.Id}' value_ref '{assertion.ValueRef}' requires a compiler-derived identity output for the exact referenced entity.");
        }
    }

    private static bool IsActorPrincipalEvidenceValue(object? value) => value switch
    {
        string text => BundleAcceptanceActorPrincipalToken.IsCandidate(text),
        JsonElement { ValueKind: JsonValueKind.String } element =>
            BundleAcceptanceActorPrincipalToken.IsCandidate(element.GetString()),
        _ => false
    };

    private async Task ExecuteAssertionAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestContext context,
        BundleAcceptanceAssertion assertion,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        switch (assertion.Type.Trim().ToLowerInvariant())
        {
            case "record_exists":
                {
                    var count = await MatchingRecordCountAsync(
                        tenant,
                        context,
                        assertion.Entity,
                        assertion.Where,
                        cancellationToken);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = "at_least_one",
                        ActualValue = count,
                        RowCount = count <= int.MaxValue ? (int)count : null
                    };
                    if (count == 0)
                    {
                        failures.Add($"Expected record to exist for entity '{assertion.Entity}'.");
                    }

                    break;
                }
            case "record_count":
                {
                    var count = await MatchingRecordCountAsync(
                        tenant,
                        context,
                        assertion.Entity,
                        assertion.Where,
                        cancellationToken);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = assertion.Count,
                        ActualValue = count,
                        RowCount = count <= int.MaxValue ? (int)count : null
                    };
                    if (count != assertion.Count)
                    {
                        failures.Add($"Expected {assertion.Count} records for entity '{assertion.Entity}' but found {count}.");
                    }

                    break;
                }
            case "record_field_equals":
                {
                    var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
                    var field = RuntimeSeedRunner.ResolveField(entity, assertion.Field)
                                ?? throw new RuntimeBundleTestException($"Assertion references unknown field '{assertion.Field}' on entity '{entity.Id}'.");
                    var expected = ResolveExpectedValue(
                        context,
                        assertion.Value,
                        assertion.ValueRef,
                        field.ReferencesEntity);
                    var evidenceKind = EvidenceValueKindForField(field, assertion);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = expected,
                        ExpectedValueKind = evidenceKind,
                        HasActualValue = false
                    };
                    var record = await records.GetRecordAsync(
                                     tenant,
                                     entity.Key,
                                     ResolveRecordId(context, assertion.Record, entity.Id),
                                     cancellationToken: cancellationToken)
                                 ?? throw new RuntimeBundleTestException($"Assertion record ref '{assertion.Record.Ref}' was not found.");
                    var actual = record.GetValueOrDefault(field.ColumnName);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = expected,
                        ExpectedValueKind = evidenceKind,
                        ActualValue = actual,
                        ActualValueKind = evidenceKind
                    };
                    if (!ValuesEqual(actual, expected))
                    {
                        failures.Add($"Expected {entity.Id}.{field.Id} to equal '{expected}' but was '{actual}'.");
                    }

                    break;
                }
            case "workflow_instance_count":
                {
                    var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
                    var recordId = ResolveRecordId(context, assertion.Record, entity.Id);
                    if (string.IsNullOrWhiteSpace(assertion.Workflow) || assertion.Count is null or < 0)
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' has an invalid workflow_instance_count contract.");
                    }


                    var matchingWorkflows = (await workflows.ListWorkflowsAsync(tenant, cancellationToken))
                        .Where(workflow =>
                            workflow.Id.Equals(assertion.Workflow, StringComparison.Ordinal) &&
                            workflow.Entity.Equals(entity.Id, StringComparison.Ordinal))
                        .Take(2)
                        .ToArray();
                    if (matchingWorkflows.Length != 1)
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' must reference one exact workflow for entity '{entity.Id}'.");
                    }

                    if (!context.TryGetStep(assertion.AfterStep, out var checkpoint))
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' must use its exact workflow-producing step as after_step.");
                    }

                    var checkpointMatches = false;
                    if (checkpoint.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase))
                    {
                        var checkpointWorkflows = (await workflows.ListWorkflowsAsync(tenant, cancellationToken))
                            .Where(workflow =>
                                workflow.Entity.Equals(checkpoint.Entity, StringComparison.Ordinal) &&
                                workflow.Trigger?.Action.Equals(checkpoint.Action, StringComparison.Ordinal) == true)
                            .Take(2)
                            .ToArray();
                        checkpointMatches = checkpointWorkflows is [{ } checkpointWorkflow] &&
                                            checkpointWorkflow.Id.Equals(assertion.Workflow, StringComparison.Ordinal) &&
                                            string.IsNullOrWhiteSpace(assertion.WorkflowRef);
                    }
                    else if (checkpoint.Type.Equals(
                                 BundleAcceptanceStepTypes.RunDueSchedule,
                                 StringComparison.OrdinalIgnoreCase) &&
                             assertion.WorkflowRef.Equals(checkpoint.Id, StringComparison.Ordinal))
                    {
                        if (checkpoint.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.Due, StringComparison.Ordinal) &&
                            context.WorkflowStarts.TryGetValue(checkpoint.Id, out var scheduled))
                        {
                            checkpointMatches = scheduled.Instance.WorkflowId.Equals(assertion.Workflow, StringComparison.Ordinal) &&
                                                scheduled.Instance.EntityId.Equals(assertion.Entity, StringComparison.Ordinal) &&
                                                scheduled.Instance.RecordId == recordId;
                        }
                        else if (checkpoint.ScheduleTime.Equals(
                                     BundleAcceptanceScheduleTimes.BeforeDue,
                                     StringComparison.Ordinal) &&
                                 assertion.Count == 0 &&
                                 context.ScheduleRuns.TryGetValue(checkpoint.Id, out var scheduleRun))
                        {
                            checkpointMatches = scheduleRun.WorkflowId.Equals(assertion.Workflow, StringComparison.Ordinal) &&
                                                scheduleRun.EntityId.Equals(assertion.Entity, StringComparison.Ordinal) &&
                                                scheduleRun.RecordId == recordId &&
                                                scheduleRun.Job.Status.Equals(RuntimeScheduleJobStatuses.Ready, StringComparison.Ordinal) &&
                                                scheduleRun.Job.WorkflowInstanceId is null;
                        }
                    }

                    if (!checkpointMatches ||
                        !checkpoint.Entity.Equals(assertion.Entity, StringComparison.Ordinal) ||
                        !string.Equals(checkpoint.Record?.Ref, assertion.Record?.Ref, StringComparison.Ordinal))
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' must bind its workflow-producing checkpoint to that exact workflow, entity, and record ref.");
                    }

                    await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                    select count(*)
                    from _runtime.workflow_instances
                    where workflow_id = @workflow
                      and entity_id = @entity
                      and record_id = @recordId
                    """;
                    command.Parameters.AddWithValue("workflow", assertion.Workflow);
                    command.Parameters.AddWithValue("entity", entity.Id);
                    command.Parameters.AddWithValue("recordId", recordId);
                    var actual = Convert.ToInt32(
                        await command.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = assertion.Count.Value,
                        ExpectedValueKind = BundleAcceptanceEvidenceValueKinds.Integer,
                        ActualValue = actual,
                        ActualValueKind = BundleAcceptanceEvidenceValueKinds.Integer,
                        RowCount = actual
                    };
                    if (actual != assertion.Count.Value)
                    {
                        failures.Add(
                            $"Expected {assertion.Count.Value} workflow instances for workflow '{assertion.Workflow}' on {entity.Id} ref '{assertion.Record?.Ref}' but found {actual}.");
                    }

                    break;
                }
            case "workflow_status":
                {
                    var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
                    var recordId = ResolveRecordId(context, assertion.Record, entity.Id);
                    var instances = await workflows.ListInstancesAsync(tenant, SystemTestUser(), cancellationToken);
                    RuntimeWorkflowInstance? observedInstance;
                    RuntimeWorkflowInstance? matchingInstance;
                    if (context.IsV3)
                    {
                        if (!context.WorkflowStarts.TryGetValue(assertion.WorkflowRef, out var started))
                        {
                            throw new RuntimeBundleTestException(
                                $"Assertion '{assertion.Id}' references unknown workflow_ref '{assertion.WorkflowRef}'.");
                        }

                        observedInstance = instances.SingleOrDefault(instance => instance.Id == started.Instance.Id);
                        matchingInstance = observedInstance is not null &&
                                           observedInstance.EntityId.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) &&
                                           observedInstance.RecordId == recordId &&
                                           observedInstance.WorkflowId.Equals(assertion.Workflow, StringComparison.OrdinalIgnoreCase) &&
                                           observedInstance.Status.Equals(assertion.Status, StringComparison.OrdinalIgnoreCase)
                            ? observedInstance
                            : null;
                    }
                    else
                    {
                        var compatibleInstances = instances.Where(instance =>
                            instance.EntityId.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) &&
                            instance.RecordId == recordId &&
                            instance.WorkflowId.Equals(assertion.Workflow, StringComparison.OrdinalIgnoreCase)).ToArray();
                        matchingInstance = compatibleInstances.FirstOrDefault(instance =>
                            instance.Status.Equals(assertion.Status, StringComparison.OrdinalIgnoreCase));
                        observedInstance = matchingInstance ?? compatibleInstances.FirstOrDefault();
                    }

                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = assertion.Status,
                        ActualValue = observedInstance?.Status,
                        WorkflowInstanceId = observedInstance?.Id,
                        WorkflowStatus = observedInstance?.Status ?? string.Empty
                    };
                    if (matchingInstance is null)
                    {
                        failures.Add($"Expected workflow '{assertion.Workflow}' for {entity.Id} ref '{assertion.Record.Ref}' to be '{assertion.Status}'.");
                    }

                    break;
                }
            case "task_exists":
            case "task_count":
                {
                    var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
                    var recordId = ResolveRecordId(context, assertion.Record, entity.Id);
                    var tasks = (await workflows.ListTasksAsync(tenant, SystemTestUser(), includeCompleted: true, cancellationToken))
                        .Where(task => task.EntityId.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) && task.RecordId == recordId)
                        .ToArray();
                    if (context.IsV3)
                    {
                        if (!context.WorkflowStarts.TryGetValue(assertion.WorkflowRef, out var started))
                        {
                            throw new RuntimeBundleTestException(
                                $"Assertion '{assertion.Id}' references unknown workflow_ref '{assertion.WorkflowRef}'.");
                        }

                        tasks = tasks.Where(task => task.InstanceId == started.Instance.Id).ToArray();
                    }
                    var exactInstanceTasks = tasks;
                    if (!string.IsNullOrWhiteSpace(assertion.TaskStep))
                    {
                        tasks = tasks.Where(task => task.StepId.Equals(assertion.TaskStep, StringComparison.Ordinal)).ToArray();
                    }

                    if (!string.IsNullOrWhiteSpace(assertion.Status))
                    {
                        tasks = tasks.Where(task => task.Status.Equals(assertion.Status, StringComparison.OrdinalIgnoreCase)).ToArray();
                    }

                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = assertion.Type.Equals("task_count", StringComparison.OrdinalIgnoreCase)
                            ? assertion.Count
                            : "at_least_one",
                        ActualValue = tasks.Length,
                        TaskIds = exactInstanceTasks.Select(task => task.Id).ToArray(),
                        TaskSteps = exactInstanceTasks.Select(task => task.StepId).ToArray(),
                        TaskStatuses = exactInstanceTasks.Select(task => task.Status).ToArray()
                    };
                    if (assertion.Type.Equals("task_exists", StringComparison.OrdinalIgnoreCase) && tasks.Length == 0)
                    {
                        failures.Add($"Expected task to exist for {entity.Id} ref '{assertion.Record.Ref}'.");
                    }
                    else if (assertion.Type.Equals("task_count", StringComparison.OrdinalIgnoreCase) && tasks.Length != assertion.Count)
                    {
                        failures.Add($"Expected {assertion.Count} tasks for {entity.Id} ref '{assertion.Record.Ref}' but found {tasks.Length}.");
                    }

                    break;
                }
            case "notification_exists":
                {
                    if (!context.IsV3)
                    {
                        throw new RuntimeBundleTestException(
                            "notification_exists requires the v3 acceptance-test contract.");
                    }

                    if (string.IsNullOrWhiteSpace(assertion.Entity) ||
                        string.IsNullOrWhiteSpace(assertion.Record?.Ref) ||
                        string.IsNullOrWhiteSpace(assertion.WorkflowRef) ||
                        string.IsNullOrWhiteSpace(assertion.TaskStep) ||
                        string.IsNullOrWhiteSpace(assertion.AfterStep) ||
                        assertion.AfterStep.Equals(BundleAcceptanceAssertionCheckpoints.Setup, StringComparison.Ordinal))
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' requires exact entity, record.ref, workflow_ref, task_step, and after_step values.");
                    }

                    if (!context.WorkflowStarts.Keys.Contains(assertion.WorkflowRef, StringComparer.Ordinal) ||
                        !context.WorkflowStarts.TryGetValue(assertion.WorkflowRef, out var started))
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' references unknown workflow_ref '{assertion.WorkflowRef}'.");
                    }

                    if (!context.NotificationBoundariesByStep.TryGetValue(assertion.AfterStep, out var notificationBoundary))
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' has no notification boundary for exact checkpoint '{assertion.AfterStep}'.");
                    }

                    if (!context.NotificationCheckpointsByStep.TryGetValue(assertion.AfterStep, out var notificationCheckpoint))
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' has no completed notification checkpoint '{assertion.AfterStep}'.");
                    }

                    var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
                    var recordId = ResolveRecordId(context, assertion.Record, entity.Id);
                    if (!started.Instance.EntityId.Equals(entity.Id, StringComparison.Ordinal) ||
                        started.Instance.RecordId != recordId)
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' workflow_ref does not target its exact entity and record ref.");
                    }

                    var notifications = await MatchingNotificationsAsync(
                        tenant,
                        notificationBoundary,
                        notificationCheckpoint,
                        started.Instance.Id,
                        entity,
                        recordId,
                        assertion.TaskStep,
                        cancellationToken);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = 1,
                        ActualValue = notifications.Count,
                        WorkflowInstanceId = started.Instance.Id,
                        NotificationIds = notifications.Select(notification => notification.Id).ToArray(),
                        Notifications = notifications
                    };
                    if (notifications.Count != 1)
                    {
                        failures.Add(
                            $"Expected exactly one fresh notification from workflow_ref '{assertion.WorkflowRef}' step '{assertion.TaskStep}' for {entity.Id} ref '{assertion.Record.Ref}' at checkpoint '{assertion.AfterStep}', but found {notifications.Count}.");
                    }

                    break;
                }
            case "report_has_rows":
                {
                    var report = await ResolveAssertionReportAsync(tenant, context, assertion, cancellationToken);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = "at_least_one",
                        ActualValue = report.Rows.Count,
                        RowCount = report.Rows.Count,
                        ReportOutputHash = ComputeReportOutputHash(report)
                    };
                    if (report.Rows.Count == 0)
                    {
                        failures.Add($"Expected report '{assertion.Report}' to return rows.");
                    }

                    break;
                }
            case "report_metric_equals":
                {
                    var report = await ResolveAssertionReportAsync(tenant, context, assertion, cancellationToken);
                    var metricColumn = report.Columns.SingleOrDefault(column =>
                        column.Key.Equals(assertion.Metric, StringComparison.Ordinal));
                    var expected = ResolveExpectedValue(context, assertion.Value, assertion.ValueRef);
                    var expectedKind = ExpectedEvidenceValueKindForReportColumn(
                        metricColumn,
                        assertion,
                        expected);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = expected,
                        ExpectedValueKind = expectedKind,
                        HasActualValue = false
                    };
                    ValidateReportMetricValueRef(context, assertion, metricColumn);
                    var metricValues = report.Rows
                        .Where(row => row.ContainsKey(assertion.Metric))
                        .Select(row => row[assertion.Metric])
                        .ToArray();
                    var actualKind = EvidenceValueKindForReportColumn(metricColumn, expected);
                    var kindsMatch = !context.IsV3 || expectedKind.Equals(actualKind, StringComparison.Ordinal);
                    var metricMatched = kindsMatch && metricValues.Any(actual => ValuesEqual(actual, expected));
                    var observedMetric = metricMatched
                        ? metricValues.First(actual => ValuesEqual(actual, expected))
                        : metricValues.FirstOrDefault();
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = expected,
                        ExpectedValueKind = expectedKind,
                        ActualValue = context.IsV3 ? observedMetric : BoundedReportMetricValue(observedMetric),
                        ActualValueKind = actualKind,
                        RowCount = report.Rows.Count,
                        ReportMetricValues = context.IsV3
                            ? metricValues.Take(MaximumReportMetricEvidenceValues).ToArray()
                            : metricValues
                                .Take(MaximumReportMetricEvidenceValues)
                                .Select(BoundedReportMetricValue)
                                .ToArray(),
                        ReportMetricValueKind = actualKind,
                        ReportOutputHash = ComputeReportOutputHash(report)
                    };
                    if (!metricMatched)
                    {
                        failures.Add($"Expected report '{assertion.Report}' metric '{assertion.Metric}' to contain '{expected}'.");
                    }

                    break;
                }
            case "report_row_count":
                {
                    RequireV3ExactReportAssertion(context, assertion);
                    if (assertion.Count is null or < 0)
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' requires a non-negative exact report row count.");
                    }

                    var report = await ResolveAssertionReportAsync(tenant, context, assertion, cancellationToken);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = assertion.Count.Value,
                        ActualValue = report.Rows.Count,
                        RowCount = report.Rows.Count,
                        ReportOutputHash = ComputeReportOutputHash(report),
                        ReportTruncated = report.IsTruncated,
                        ReportAppliedLimit = report.AppliedLimit
                    };
                    if (report.IsTruncated)
                    {
                        failures.Add(
                            $"Report '{assertion.Report}' was truncated at {report.AppliedLimit} rows and cannot prove an exact row count.");
                    }
                    else if (report.Rows.Count != assertion.Count.Value)
                    {
                        failures.Add(
                            $"Expected report '{assertion.Report}' to return exactly {assertion.Count.Value} rows but found {report.Rows.Count}.");
                    }

                    break;
                }
            case "report_record_absent":
                {
                    RequireV3ExactReportAssertion(context, assertion);
                    var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
                    if (!context.Refs.TryGetValue(assertion.Record?.Ref ?? string.Empty, out var record) ||
                        !record.Entity.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) ||
                        !record.EntityKey.Equals(entity.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RuntimeBundleTestException(
                            $"Assertion '{assertion.Id}' record ref '{assertion.Record?.Ref}' is not an exact '{assertion.Entity}' record.");
                    }

                    var report = await ResolveAssertionReportAsync(tenant, context, assertion, cancellationToken);
                    var identityColumn = report.Columns.SingleOrDefault(column =>
                        column.Key.Equals(assertion.Metric, StringComparison.Ordinal));
                    var hasIdentityMetric = identityColumn is
                    {
                        Kind: RuntimeReportColumnKinds.Identity,
                        IdentityEntityId: not null
                    } && identityColumn.IdentityEntityId.Equals(entity.Id, StringComparison.OrdinalIgnoreCase);
                    var everyRowHasIdentityMetric = report.Rows.All(row => row.ContainsKey(assertion.Metric));
                    var metricValues = hasIdentityMetric
                        ? report.Rows
                            .Where(row => row.ContainsKey(assertion.Metric))
                            .Select(row => row[assertion.Metric])
                            .ToArray()
                        : [];
                    var matchingRecordCount = metricValues.Count(value => ValuesEqual(value, record.Id));
                    var metricKind = EvidenceValueKindForReportColumn(
                        identityColumn,
                        metricValues.FirstOrDefault());
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = 0,
                        ExpectedValueKind = BundleAcceptanceEvidenceValueKinds.Integer,
                        ActualValue = matchingRecordCount,
                        ActualValueKind = BundleAcceptanceEvidenceValueKinds.Integer,
                        RowCount = report.Rows.Count,
                        ReportMetricValues = context.IsV3
                            ? metricValues.Take(MaximumReportMetricEvidenceValues).ToArray()
                            : metricValues
                                .Take(MaximumReportMetricEvidenceValues)
                                .Select(BoundedReportMetricValue)
                                .ToArray(),
                        ReportMetricValueKind = metricKind,
                        ReportOutputHash = ComputeReportOutputHash(report),
                        ReportTruncated = report.IsTruncated,
                        ReportAppliedLimit = report.AppliedLimit
                    };
                    if (report.IsTruncated)
                    {
                        failures.Add(
                            $"Report '{assertion.Report}' was truncated at {report.AppliedLimit} rows and cannot prove exact record absence.");
                    }
                    else if (!hasIdentityMetric)
                    {
                        failures.Add(
                            $"Report '{assertion.Report}' output '{assertion.Metric}' is not a compiler-derived direct identity for entity '{entity.Id}'.");
                    }
                    else if (!everyRowHasIdentityMetric)
                    {
                        failures.Add(
                            $"Report '{assertion.Report}' omitted identity metric '{assertion.Metric}' from one or more rows required by assertion '{assertion.Id}'.");
                    }
                    else if (matchingRecordCount != 0)
                    {
                        failures.Add(
                            $"Expected report '{assertion.Report}' metric '{assertion.Metric}' to exclude record ref '{assertion.Record?.Ref}' but found {matchingRecordCount} matching rows.");
                    }

                    break;
                }
            case "dashboard_metric_equals":
                {
                    RequireV3ExactReportAssertion(context, assertion);
                    var report = await ResolveAssertionReportAsync(tenant, context, assertion, cancellationToken);
                    var expected = ResolveExpectedValue(context, assertion.Value, assertion.ValueRef);
                    var dashboard = await reports.GetDashboardAsync(tenant, cancellationToken);
                    var widgets = dashboard.Widgets
                        .Where(widget => widget.Key.Equals(assertion.Widget, StringComparison.Ordinal))
                        .ToArray();
                    var widget = widgets.Length == 1 ? widgets[0] : null;
                    var bindingMatches = widget is not null &&
                                         widget.Report.Equals(assertion.Report, StringComparison.Ordinal) &&
                                         widget.Metric.Equals(assertion.Metric, StringComparison.Ordinal);
                    var metricColumn = report.Columns.SingleOrDefault(column =>
                        column.Key.Equals(assertion.Metric, StringComparison.Ordinal));
                    var hasMetric = metricColumn?.Kind.Equals(
                        RuntimeReportColumnKinds.Metric,
                        StringComparison.Ordinal) == true;
                    var everyRowHasMetric = report.Rows.All(row => row.ContainsKey(assertion.Metric));
                    var metricValues = hasMetric
                        ? report.Rows
                            .Where(row => row.ContainsKey(assertion.Metric))
                            .Select(row => row[assertion.Metric])
                            .ToArray()
                        : [];
                    var actual = metricValues.Length == 1 ? metricValues[0] : null;
                    var expectedKind = ExpectedEvidenceValueKindForReportColumn(
                        metricColumn,
                        assertion,
                        actual ?? expected);
                    var actualKind = EvidenceValueKindForReportColumn(metricColumn, actual ?? expected);
                    var kindsMatch = !context.IsV3 || expectedKind.Equals(actualKind, StringComparison.Ordinal);
                    var outputMatches = hasMetric && everyRowHasMetric && report.Rows.Count == 1 &&
                                        metricValues.Length == 1 && kindsMatch && ValuesEqual(actual, expected);
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = expected,
                        ExpectedValueKind = expectedKind,
                        ActualValue = context.IsV3 ? actual : BoundedReportMetricValue(actual),
                        ActualValueKind = actualKind,
                        DashboardWidgetReport = widget?.Report ?? string.Empty,
                        DashboardWidgetMetric = widget?.Metric ?? string.Empty,
                        RowCount = report.Rows.Count,
                        ReportMetricValues = context.IsV3
                            ? metricValues.Take(MaximumReportMetricEvidenceValues).ToArray()
                            : metricValues
                                .Take(MaximumReportMetricEvidenceValues)
                                .Select(BoundedReportMetricValue)
                                .ToArray(),
                        ReportMetricValueKind = actualKind,
                        ReportOutputHash = ComputeReportOutputHash(report),
                        ReportTruncated = report.IsTruncated,
                        ReportAppliedLimit = report.AppliedLimit
                    };
                    if (widgets.Length != 1)
                    {
                        failures.Add(
                            $"Expected exactly one dashboard widget '{assertion.Widget}' but found {widgets.Length}.");
                    }
                    else if (!bindingMatches)
                    {
                        failures.Add(
                            $"Dashboard widget '{assertion.Widget}' binds report '{widget!.Report}' metric '{widget.Metric}', not exact report '{assertion.Report}' metric '{assertion.Metric}'.");
                    }

                    if (report.IsTruncated)
                    {
                        failures.Add(
                            $"Report '{assertion.Report}' was truncated at {report.AppliedLimit} rows and cannot prove an exact dashboard value.");
                    }
                    else if (!hasMetric)
                    {
                        failures.Add(
                            $"Report '{assertion.Report}' does not expose exact dashboard metric '{assertion.Metric}'.");
                    }
                    else if (!everyRowHasMetric || report.Rows.Count != 1 || metricValues.Length != 1)
                    {
                        failures.Add(
                            $"Expected report '{assertion.Report}' dashboard metric '{assertion.Metric}' on its one exact output row but found {report.Rows.Count} rows and {metricValues.Length} metric values.");
                    }
                    else if (!outputMatches)
                    {
                        failures.Add(
                            $"Expected dashboard widget '{assertion.Widget}' metric '{assertion.Metric}' to equal '{expected}' but was '{actual}'.");
                    }

                    break;
                }
            case "dashboard_widget_exists":
                {
                    var dashboard = await reports.GetDashboardAsync(tenant, cancellationToken);
                    var exists = dashboard.Widgets.Any(widget =>
                            widget.Key.Equals(assertion.Widget, StringComparison.OrdinalIgnoreCase) &&
                            widget.Report.Equals(assertion.Report, StringComparison.OrdinalIgnoreCase) &&
                            widget.Metric.Equals(assertion.Metric, StringComparison.OrdinalIgnoreCase));
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = true,
                        ActualValue = exists
                    };
                    if (!exists)
                    {
                        failures.Add(
                            $"Expected dashboard widget '{assertion.Widget}' to bind report '{assertion.Report}' metric '{assertion.Metric}'.");
                    }

                    break;
                }
            case "audit_event_exists":
                {
                    var audit = await records.ListAuditEventsAsync(tenant, 200, cancellationToken);
                    var auditBoundary = context.IsV3 &&
                                        context.AuditEventBoundariesByStep.TryGetValue(assertion.AfterStep, out var exactBoundary)
                        ? exactBoundary
                        : context.AuditEventBoundaryId;
                    var candidates = audit.Where(@event =>
                        @event.Id > auditBoundary &&
                        @event.EventType.Equals(assertion.Event, StringComparison.OrdinalIgnoreCase));
                    var hasEntity = !string.IsNullOrWhiteSpace(assertion.Entity);
                    var hasRecordRef = !string.IsNullOrWhiteSpace(assertion.Record?.Ref);
                    if (hasEntity != hasRecordRef)
                    {
                        throw new RuntimeBundleTestException(
                            "audit_event_exists must provide both entity and record.ref for exact correlation, or neither for fresh-run correlation.");
                    }

                    if (hasEntity)
                    {
                        var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
                        var recordSelector = assertion.Record
                                             ?? throw new RuntimeBundleTestException(
                                                 "audit_event_exists record selector was missing after correlation validation.");
                        var recordId = ResolveRecordId(context, recordSelector, entity.Id);
                        candidates = candidates.Where(@event => AuditMatchesRecord(@event.Metadata, entity, recordId));
                    }

                    if (!string.IsNullOrWhiteSpace(assertion.AuditStep))
                    {
                        candidates = candidates.Where(@event => AuditMatchesStep(@event.Metadata, assertion.AuditStep));
                    }

                    var matchedAuditIds = candidates.Select(candidate => candidate.Id).ToArray();
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = "at_least_one",
                        ActualValue = matchedAuditIds.Length,
                        AuditEventIds = matchedAuditIds
                    };
                    if (matchedAuditIds.Length == 0)
                    {
                        failures.Add(hasEntity
                            ? $"Expected fresh audit event '{assertion.Event}' for {assertion.Entity} ref '{assertion.Record?.Ref}' to exist."
                            : $"Expected fresh audit event '{assertion.Event}' to exist after the acceptance test boundary.");
                    }

                    break;
                }
            case "permission_allowed":
                {
                    var user = UserForRole(assertion.PermissionRole);
                    var allowed = assertion.PermissionScope.Trim().ToLowerInvariant() switch
                    {
                        PermissionGrantScopes.Field => await IsFieldPermissionAllowedAsync(
                            tenant,
                            user,
                            assertion,
                            cancellationToken),
                        PermissionGrantScopes.Page => await permissions.IsPageAllowedAsync(
                            tenant,
                            user,
                            assertion.PermissionTarget,
                            assertion.Entity,
                            cancellationToken),
                        PermissionGrantScopes.Action => await IsNamedActionPermissionAllowedAsync(
                            tenant,
                            user,
                            assertion,
                            cancellationToken),
                        _ => throw new RuntimeBundleTestException(
                            $"Unsupported granular permission assertion scope '{assertion.PermissionScope}'.")
                    };
                    context.AssertionEvidence[assertion.Id] = new RuntimeBundleTestAssertionEvidence
                    {
                        ExpectedValue = true,
                        ActualValue = allowed
                    };
                    if (!allowed)
                    {
                        failures.Add(
                            $"Expected role '{assertion.PermissionRole}' to have {assertion.PermissionScope} permission '{assertion.PermissionAction}' for '{assertion.Entity}:{assertion.PermissionTarget}'.");
                    }

                    break;
                }
            default:
                throw new RuntimeBundleTestException($"Unsupported acceptance assertion type '{assertion.Type}'.");
        }
    }

    private async Task<bool> IsFieldPermissionAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        BundleAcceptanceAssertion assertion,
        CancellationToken cancellationToken)
    {
        var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
        return await permissions.IsFieldAllowedAsync(
            tenant,
            user,
            assertion.PermissionAction,
            entity,
            assertion.PermissionTarget,
            cancellationToken);
    }

    private async Task<bool> IsNamedActionPermissionAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        BundleAcceptanceAssertion assertion,
        CancellationToken cancellationToken)
    {
        var entity = await ResolveEntityAsync(tenant, assertion.Entity, cancellationToken);
        return await permissions.IsNamedActionAllowedAsync(
            tenant,
            user,
            entity,
            assertion.PermissionTarget,
            cancellationToken);
    }

    private async Task<long> LatestAuditEventIdAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var latest = await records.ListAuditEventsAsync(tenant, 1, cancellationToken);
        return latest.Count == 0 ? 0 : latest[0].Id;
    }

    private async Task<long> LatestNotificationIdAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select coalesce(max(id), 0) from _runtime.notification_outbox";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<RuntimeBundleTestNotificationObservation>> MatchingNotificationsAsync(
        ResolvedTenantEnvironment tenant,
        long boundaryId,
        long checkpointId,
        long workflowInstanceId,
        RuntimeEntityDefinition entity,
        long recordId,
        string workflowStepId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, task_id, notification_type, recipient_role, recipient_user_id
            from _runtime.notification_outbox
            where id > @boundaryId
              and id <= @checkpointId
              and instance_id = @instanceId
              and payload ->> 'entity' = @entity
              and payload ->> 'recordId' = @recordId
              and payload ->> 'stepId' = @stepId
            order by id
            """;
        command.Parameters.AddWithValue("boundaryId", boundaryId);
        command.Parameters.AddWithValue("checkpointId", checkpointId);
        command.Parameters.AddWithValue("instanceId", workflowInstanceId);
        command.Parameters.AddWithValue("entity", entity.Key);
        command.Parameters.AddWithValue("recordId", recordId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("stepId", workflowStepId);

        var notifications = new List<RuntimeBundleTestNotificationObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            notifications.Add(new RuntimeBundleTestNotificationObservation(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return notifications;
    }

    private static bool AuditMatchesRecord(
        JsonElement metadata,
        RuntimeEntityDefinition entity,
        long recordId)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("entity", out var entityValue) ||
            !metadata.TryGetProperty("recordId", out var recordValue))
        {
            return false;
        }

        var eventEntity = entityValue.ValueKind == JsonValueKind.String
            ? entityValue.GetString()
            : entityValue.ToString();
        var entityMatches = string.Equals(eventEntity, entity.Key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventEntity, entity.Id, StringComparison.OrdinalIgnoreCase);
        var recordMatches = recordValue.ValueKind == JsonValueKind.Number
            ? recordValue.TryGetInt64(out var numericId) && numericId == recordId
            : long.TryParse(recordValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textId) &&
              textId == recordId;
        return entityMatches && recordMatches;
    }

    private static bool AuditMatchesStep(JsonElement metadata, string auditStep) =>
        metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty("stepId", out var stepValue) &&
        string.Equals(
            stepValue.ValueKind == JsonValueKind.String ? stepValue.GetString() : stepValue.ToString(),
            auditStep,
            StringComparison.Ordinal);

    private async Task<long> MatchingRecordCountAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestContext context,
        string entityKey,
        BundleAcceptanceWhere where,
        CancellationToken cancellationToken)
    {
        var entity = await ResolveEntityAsync(tenant, entityKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(where.Field))
        {
            var list = await records.ListRecordsAsync(
                tenant,
                entity.Key,
                new RuntimeRecordQuery(PageSize: 1),
                cancellationToken);
            return list.Total;
        }

        var field = RuntimeSeedRunner.ResolveField(entity, where.Field)
                    ?? throw new RuntimeBundleTestException($"Assertion references unknown field '{where.Field}' on entity '{entity.Id}'.");
        var expected = ResolveExpectedValue(
            context,
            where.Value,
            where.ValueRef,
            field.ReferencesEntity);
        var normalizedOp = string.IsNullOrWhiteSpace(where.Op) ? "eq" : where.Op.Trim().ToLowerInvariant();
        var column = QuoteSqlIdentifier(field.ColumnName);
        string predicate;
        object? parameterValue = null;
        switch (normalizedOp)
        {
            case "exists":
                predicate = $"{column} is not null";
                break;
            case "not_exists":
                predicate = $"{column} is null";
                break;
            case "eq" when expected is null:
                predicate = $"{column} is null";
                break;
            case "neq" when expected is null:
                predicate = $"{column} is not null";
                break;
            case "eq":
                predicate = $"{column} = @expected";
                parameterValue = ConvertAssertionWhereValue(field, expected);
                break;
            case "neq":
                predicate = $"({column} is null or {column} <> @expected)";
                parameterValue = ConvertAssertionWhereValue(field, expected);
                break;
            case "gt":
            case "gte":
            case "lt":
            case "lte":
                if (expected is null)
                {
                    throw new RuntimeBundleTestException(
                        $"Assertion where op '{where.Op}' requires a non-null value.");
                }

                predicate = $"{column} {normalizedOp switch
                {
                    "gt" => ">",
                    "gte" => ">=",
                    "lt" => "<",
                    _ => "<="
                }} @expected";
                parameterValue = ConvertAssertionWhereValue(field, expected);
                break;
            default:
                throw new RuntimeBundleTestException($"Unsupported assertion where op '{where.Op}'.");
        }

        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select count(*)
            from {QuoteSqlIdentifier(entity.SchemaName)}.{QuoteSqlIdentifier(entity.TableName)}
            where archived_at is null
              and ({predicate})
            """;
        if (parameterValue is not null)
        {
            command.Parameters.AddWithValue("expected", parameterValue);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static object ConvertAssertionWhereValue(RuntimeFieldDefinition field, object expected)
    {
        expected = expected is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new RuntimeBundleTestException(
                    $"Assertion where value for field '{field.Id}' must be a scalar.")
            }
            : expected;

        try
        {
            return field.FieldType.ToLowerInvariant() switch
            {
                "string" or "text" => Convert.ToString(expected, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                "enum" => ConvertAssertionEnumValue(field, expected),
                "integer" or "relation" => Convert.ToInt64(expected, CultureInfo.InvariantCulture),
                "number" or "decimal" or "money" => Convert.ToDecimal(expected, CultureInfo.InvariantCulture),
                "boolean" => Convert.ToBoolean(expected, CultureInfo.InvariantCulture),
                "date" when expected is DateOnly date => date,
                "date" => DateOnly.Parse(
                    Convert.ToString(expected, CultureInfo.InvariantCulture) ?? string.Empty,
                    CultureInfo.InvariantCulture),
                "datetime" when expected is DateTimeOffset dateTime => dateTime,
                "datetime" => DateTimeOffset.Parse(
                    Convert.ToString(expected, CultureInfo.InvariantCulture) ?? string.Empty,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal),
                _ => throw new RuntimeBundleTestException(
                    $"Assertion where field '{field.Id}' uses unsupported type '{field.FieldType}'.")
            };
        }
        catch (FormatException)
        {
            throw new RuntimeBundleTestException(
                $"Assertion where value is invalid for field '{field.Id}' ({field.FieldType}).");
        }
        catch (InvalidCastException)
        {
            throw new RuntimeBundleTestException(
                $"Assertion where value is invalid for field '{field.Id}' ({field.FieldType}).");
        }
        catch (OverflowException)
        {
            throw new RuntimeBundleTestException(
                $"Assertion where value is out of range for field '{field.Id}' ({field.FieldType}).");
        }
    }

    private static string ConvertAssertionEnumValue(RuntimeFieldDefinition field, object expected)
    {
        var value = Convert.ToString(expected, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var option = field.Options.FirstOrDefault(candidate =>
            candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
        return option ?? throw new RuntimeBundleTestException(
            $"Assertion where value '{value}' is not an option for field '{field.Id}'.");
    }

    private static string QuoteSqlIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";

    private async Task<RuntimeReportResult> ExecuteReportAsync(
        ResolvedTenantEnvironment tenant,
        string reportKey,
        RuntimeUserContext user,
        CancellationToken cancellationToken,
        bool classifyPermissionDenial = false)
    {
        var report = await reports.GetReportAsync(tenant, reportKey, cancellationToken)
                     ?? throw new RuntimeBundleTestException($"Report '{reportKey}' was not found.");
        var entity = await records.GetEntityAsync(tenant, report.SourceEntity, cancellationToken)
                     ?? throw new RuntimeBundleTestException($"Report '{reportKey}' source entity '{report.SourceEntity}' was not found.");
        {
            var requiredEntities = new List<RuntimeEntityDefinition> { entity };
            foreach (var targetEntityKey in (report.Joins ?? [])
                         .Select(join => join.TargetEntity)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var target = await ResolveEntityAsync(tenant, targetEntityKey, cancellationToken);
                requiredEntities.Add(target);
            }

            RuntimeEntityDefinition? deniedEntity = null;
            foreach (var requiredEntity in requiredEntities)
            {
                if (!await permissions.IsAllowedAsync(
                        tenant,
                        user,
                        "read",
                        requiredEntity,
                        cancellationToken))
                {
                    deniedEntity ??= requiredEntity;
                }
            }

            var fieldsAllowed = await CanReadReportFieldsAsync(
                tenant,
                user,
                report,
                requiredEntities,
                cancellationToken);
            if (deniedEntity is not null || !fieldsAllowed)
            {
                var reason = deniedEntity is not null
                    ? $"Role '{user.Role}' is not allowed to 'read' entity '{deniedEntity.Id}' required by report '{reportKey}'."
                    : $"Role '{user.Role}' is not allowed to read one or more fields required by report '{reportKey}'.";
                if (classifyPermissionDenial)
                {
                    throw new RuntimeBundleTestPermissionDeniedException(
                        BundleAcceptanceExpectedOutcomeCodes.PermissionReportExecute,
                        reportKey,
                        reason);
                }

                throw new RuntimeBundleTestException(reason);
            }
        }

        return await reports.ExecuteReportAsync(tenant, user, report, entity, new RuntimeReportQuery(), cancellationToken);
    }

    private async Task<RuntimeReportResult> ResolveAssertionReportAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestContext context,
        BundleAcceptanceAssertion assertion,
        CancellationToken cancellationToken)
    {
        if (!context.IsV3)
        {
            return await ExecuteReportAsync(tenant, assertion.Report, SystemTestUser(), cancellationToken);
        }

        if (!assertion.AfterStep.Equals(assertion.ReportRef, StringComparison.Ordinal) ||
            !context.Reports.TryGetValue(assertion.ReportRef, out var report) ||
            !report.Key.Equals(assertion.Report, StringComparison.Ordinal))
        {
            throw new RuntimeBundleTestException(
                $"Assertion '{assertion.Id}' must consume the exact actor-scoped report_ref '{assertion.ReportRef}'.");
        }

        return report;
    }

    private static void RequireV3ExactReportAssertion(
        RuntimeBundleTestContext context,
        BundleAcceptanceAssertion assertion)
    {
        if (!context.IsV3)
        {
            throw new RuntimeBundleTestException(
                $"Assertion type '{assertion.Type}' requires the v3 acceptance-test contract.");
        }
    }

    private async Task<bool> CanReadReportFieldsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeReportDefinition report,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        CancellationToken cancellationToken)
    {
        var source = entities.FirstOrDefault(candidate =>
            candidate.Id.Equals(report.SourceEntity, StringComparison.OrdinalIgnoreCase) ||
            candidate.Key.Equals(report.SourceEntity, StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.Equals(report.SourceEntity, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return false;
        }

        var aliases = new Dictionary<string, RuntimeEntityDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = source
        };
        foreach (var join in report.Joins ?? [])
        {
            var target = entities.FirstOrDefault(candidate =>
                candidate.Id.Equals(join.TargetEntity, StringComparison.OrdinalIgnoreCase) ||
                candidate.Key.Equals(join.TargetEntity, StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Equals(join.TargetEntity, StringComparison.OrdinalIgnoreCase));
            if (target is null || !aliases.TryGetValue(join.FromAlias, out var from) ||
                !await permissions.IsFieldAllowedAsync(
                    tenant,
                    user,
                    GranularPermissionActions.Read,
                    from,
                    join.RelationField,
                    cancellationToken))
            {
                return false;
            }

            aliases[join.Alias] = target;
        }

        var fieldReferences = report.GroupBy.Select(group => group.Field)
            .Concat(report.Metrics.Select(metric => metric.Field))
            .Concat(report.Filters.Select(filter => filter.Field))
            .Concat(report.Metrics
                .Where(metric => metric.Filter is not null)
                .Select(metric => metric.Filter!.Field))
            .Where(field => !string.IsNullOrWhiteSpace(field));
        foreach (var fieldReference in fieldReferences)
        {
            var parts = fieldReference!.Split('.', 2, StringSplitOptions.TrimEntries);
            var alias = parts.Length == 2 ? parts[0] : "source";
            var field = parts.Length == 2 ? parts[1] : parts[0];
            if (!aliases.TryGetValue(alias, out var entity) ||
                !await permissions.IsFieldAllowedAsync(
                    tenant,
                    user,
                    GranularPermissionActions.Read,
                    entity,
                    field,
                    cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async Task EnsureAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition entity,
        string stepId,
        CancellationToken cancellationToken)
    {
        if (!await permissions.IsAllowedAsync(tenant, user, action, entity, cancellationToken))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance step '{stepId}' role '{user.Role}' is not allowed to '{action}' entity '{entity.Id}'.");
        }
    }

    private async Task EnsureAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition entity,
        string stepId,
        string denialCode,
        string denialTarget,
        CancellationToken cancellationToken)
    {
        if (!await permissions.IsAllowedAsync(tenant, user, action, entity, cancellationToken))
        {
            throw new RuntimeBundleTestPermissionDeniedException(
                denialCode,
                denialTarget,
                $"Acceptance step '{stepId}' role '{user.Role}' is not allowed to '{action}' entity '{entity.Id}'.");
        }
    }

    private async Task EnsureWritableFieldsAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IEnumerable<string> fields,
        string stepId,
        CancellationToken cancellationToken)
    {
        foreach (var field in fields)
        {
            var definition = RuntimeSeedRunner.ResolveField(entity, field)
                             ?? throw new RuntimeBundleTestException(
                                 $"Acceptance step '{stepId}' references unknown field '{field}' on entity '{entity.Id}'.");
            if (definition.Computed is not null)
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance step '{stepId}' cannot write computed field '{field}' on entity '{entity.Id}'.");
            }

            if (await permissions.IsFieldAllowedAsync(
                    tenant,
                    user,
                    GranularPermissionActions.Write,
                    entity,
                    field,
                    cancellationToken))
            {
                continue;
            }

            throw new RuntimeBundleTestPermissionDeniedException(
                BundleAcceptanceExpectedOutcomeCodes.PermissionFieldWrite,
                field,
                $"Acceptance step '{stepId}' role '{user.Role}' is not allowed to write field '{field}' on entity '{entity.Id}'.");
        }
    }

    private static bool IsNamedActionLifecycleBoundaryProbe(
        BundleAcceptanceStep step,
        RuntimeEntityDefinition entity,
        string fieldKey)
    {
        var expected = step.Expected ?? new BundleAcceptanceExpectedOutcome();
        if (!expected.Outcome.Equals(BundleAcceptanceExpectedOutcomes.BusinessRuleRejected, StringComparison.Ordinal) ||
            !expected.Code.Equals(BundleAcceptanceExpectedOutcomeCodes.LifecycleNamedActionRequired, StringComparison.Ordinal))
        {
            return false;
        }

        var field = RuntimeSeedRunner.ResolveField(entity, fieldKey);
        if (field?.Lifecycle?.RequiresNamedAction != true)
        {
            return false;
        }

        var prefix = $"{entity.Id}.";
        var canonicalField = field.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? field.Id[prefix.Length..]
            : field.Name;
        return expected.Target.Equals(canonicalField, StringComparison.Ordinal);
    }

    private async Task<RuntimeEntityDefinition> ResolveEntityAsync(
        ResolvedTenantEnvironment tenant,
        string entityKey,
        CancellationToken cancellationToken)
    {
        var entities = await records.ListEntitiesAsync(tenant, cancellationToken);
        return RuntimeSeedRunner.ResolveEntity(entities, entityKey)
               ?? throw new RuntimeBundleTestException($"Entity '{entityKey}' was not found.");
    }

    private static long ResolveRecordId(
        RuntimeBundleTestContext context,
        BundleRecordSelector selector,
        string expectedEntity) =>
        ResolveRecordReference(context, selector, expectedEntity).Id;

    private static RuntimeSeedRecordRef ResolveRecordReference(
        RuntimeBundleTestContext context,
        BundleRecordSelector selector,
        string? expectedEntity = null)
    {
        if (string.IsNullOrWhiteSpace(selector.Ref) || !context.Refs.TryGetValue(selector.Ref, out var reference))
        {
            throw new RuntimeBundleTestException($"Record ref '{selector.Ref}' was not found.");
        }

        if (!string.IsNullOrWhiteSpace(expectedEntity) &&
            !reference.Entity.Equals(expectedEntity, StringComparison.Ordinal))
        {
            throw new RuntimeBundleTestException(
                $"Record ref '{selector.Ref}' resolves to entity '{reference.Entity}', not exact entity '{expectedEntity}'.");
        }

        return reference;
    }

    private static object? ResolveExpectedValue(
        RuntimeBundleTestContext context,
        object? value,
        string valueRef,
        string? expectedReferenceEntity = null)
    {
        if (string.IsNullOrWhiteSpace(valueRef))
        {
            return context.ResolveContractValue(value);
        }

        if (!context.Refs.TryGetValue(valueRef, out var reference))
        {
            throw new RuntimeBundleTestException($"Assertion references unknown value_ref '{valueRef}'.");
        }

        if (!string.IsNullOrWhiteSpace(expectedReferenceEntity) &&
            !reference.Entity.Equals(expectedReferenceEntity, StringComparison.Ordinal))
        {
            throw new RuntimeBundleTestException(
                $"Assertion value_ref '{valueRef}' resolves to entity '{reference.Entity}', not exact entity '{expectedReferenceEntity}'.");
        }

        return reference.Id;
    }

    private async Task<RuntimeWorkflowTask> FindPendingTaskAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeBundleTestContext context,
        BundleAcceptanceStep step,
        RuntimeEntityDefinition entity,
        long recordId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.WorkflowRef) ||
            !context.WorkflowStarts.TryGetValue(step.WorkflowRef, out var started))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance step '{step.Id}' must reference an exact preceding successful workflow start or due schedule step.");
        }

        if (!started.Instance.EntityId.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) ||
            started.Instance.RecordId != recordId)
        {
            throw new RuntimeBundleTestException(
                $"Acceptance step '{step.Id}' workflow_ref '{step.WorkflowRef}' does not own the exact target record.");
        }

        var tasks = await workflows.ListTasksAsync(tenant, user, includeCompleted: false, cancellationToken);
        return SelectExactPendingTask(
            tasks,
            started.Instance.Id,
            started.Instance.WorkflowId,
            entity.Id,
            recordId,
            step.WorkflowRef);
    }

    private static bool ExpectsWorkflowTaskPermissionDenial(BundleAcceptanceStep step) =>
        step.Expected is
        {
            Outcome: BundleAcceptanceExpectedOutcomes.PermissionDenied,
            Code: BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask or
                  BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRejectTask or
                  BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment
        };

    private static RuntimeWorkflowStartResult ResolveExactReferencedPendingTaskBinding(
        RuntimeBundleTestContext context,
        BundleAcceptanceStep step,
        RuntimeEntityDefinition entity,
        long recordId)
    {
        if (!context.IsV3)
        {
            throw new RuntimeBundleTestException(
                $"Acceptance step '{step.Id}' expected workflow-task permission denial requires a V3 contract.");
        }

        if (string.IsNullOrWhiteSpace(step.WorkflowRef) ||
            !context.WorkflowStarts.TryGetValue(step.WorkflowRef, out var started))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance step '{step.Id}' must reference an exact preceding successful workflow start or due schedule step.");
        }

        var task = SelectExactReferencedPendingTask(
            started,
            entity.Id,
            recordId,
            step.WorkflowRef);
        if (!string.Equals(step.Expected.Target, task.StepId, StringComparison.Ordinal))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance step '{step.Id}' expected target does not match the exact workflow_ref task step.");
        }

        return started;
    }

    internal static RuntimeWorkflowTask SelectExactReferencedPendingTask(
        RuntimeWorkflowStartResult started,
        string entityId,
        long recordId,
        string workflowRef)
    {
        ArgumentNullException.ThrowIfNull(started);
        var instance = started.Instance;
        var task = started.Task;
        if (instance.Id <= 0 || string.IsNullOrWhiteSpace(instance.WorkflowId) ||
            !instance.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase) ||
            instance.RecordId != recordId ||
            !instance.Status.Equals("waiting", StringComparison.OrdinalIgnoreCase) ||
            task is null || task.Id <= 0 || task.InstanceId != instance.Id ||
            !task.WorkflowId.Equals(instance.WorkflowId, StringComparison.OrdinalIgnoreCase) ||
            !task.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase) ||
            task.RecordId != recordId ||
            !task.Status.Equals("pending", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(instance.CurrentStepId, task.StepId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(task.StepId))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance workflow_ref '{workflowRef}' does not bind one exact waiting instance and pending task for the target record.");
        }

        return task;
    }

    private static bool IsWorkflowTaskDecisionStep(string? stepType) =>
        stepType?.Trim().ToLowerInvariant() is "approve_task" or "reject_task";

    internal static RuntimeWorkflowStartResult? SelectReferencedWorkflowStartForObservation(
        BundleAcceptanceStep step,
        IReadOnlyDictionary<string, RuntimeWorkflowStartResult> workflowStarts)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(workflowStarts);
        return IsWorkflowTaskDecisionStep(step.Type) &&
               workflowStarts.TryGetValue(step.WorkflowRef, out var referencedStart)
            ? referencedStart
            : null;
    }

    internal static RuntimeWorkflowTask SelectExactPendingTask(
        IReadOnlyList<RuntimeWorkflowTask> tasks,
        long instanceId,
        string workflowId,
        string entityId,
        long recordId,
        string workflowRef)
    {
        var matches = tasks.Where(task =>
                task.InstanceId == instanceId &&
                task.WorkflowId.Equals(workflowId, StringComparison.OrdinalIgnoreCase) &&
                task.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase) &&
                task.RecordId == recordId &&
                task.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new RuntimeBundleTestException(
                $"No pending task found for exact workflow_ref '{workflowRef}' (instance {instanceId}, workflow '{workflowId}')."),
            _ => throw new RuntimeBundleTestException(
                $"Multiple pending tasks matched exact workflow_ref '{workflowRef}' (instance {instanceId}, workflow '{workflowId}').")
        };
    }

    private static bool MatchesWhere(object? actual, string op, object? expected)
    {
        var normalizedOp = string.IsNullOrWhiteSpace(op) ? "eq" : op.Trim().ToLowerInvariant();
        return normalizedOp switch
        {
            "eq" => ValuesEqual(actual, expected),
            "neq" => !ValuesEqual(actual, expected),
            "exists" => actual is not null,
            "not_exists" => actual is null,
            "gt" => Compare(actual, expected) > 0,
            "gte" => Compare(actual, expected) >= 0,
            "lt" => Compare(actual, expected) < 0,
            "lte" => Compare(actual, expected) <= 0,
            _ => throw new RuntimeBundleTestException($"Unsupported assertion where op '{op}'.")
        };
    }

    private const int MaximumReportMetricEvidenceValues = 16;
    private const int MaximumReportMetricStringLength = 256;

    private static object? BoundedReportMetricValue(object? value)
    {
        if (value is string text && text.Length > MaximumReportMetricStringLength)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            return $"sha256:{hash}:length:{text.Length}";
        }

        if (value is JsonElement element && element.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(element.GetRawText())));
            return $"sha256:{hash}:json";
        }

        return value;
    }

    private static string ComputeReportOutputHash(RuntimeReportResult report)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "erpgenerator.runtime-report-output-evidence.v1");
            writer.WriteString("report", report.Key);
            writer.WriteNumber("appliedLimit", report.AppliedLimit);
            writer.WriteBoolean("truncated", report.IsTruncated);
            writer.WritePropertyName("columns");
            writer.WriteStartArray();
            foreach (var column in report.Columns)
            {
                writer.WriteStartObject();
                writer.WriteString("key", column.Key);
                writer.WriteString("type", column.Type);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("rows");
            writer.WriteStartArray();
            foreach (var row in report.Rows)
            {
                writer.WriteStartObject();
                foreach (var (key, value) in row.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteCanonicalEvidenceValue(writer, value);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonicalEvidenceValue(Utf8JsonWriter writer, object? value)
    {
        if (value is null || value is DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        WriteCanonicalEvidenceElement(writer, JsonSerializer.SerializeToElement(value, JsonOptions));
    }

    private static void WriteCanonicalEvidenceElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalEvidenceElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalEvidenceElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    internal static bool ValuesEqual(object? actual, object? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (TryDecimal(actual, out var actualDecimal) && TryDecimal(expected, out var expectedDecimal))
        {
            return actualDecimal == expectedDecimal;
        }

        if (actual is bool actualBoolean && bool.TryParse(Convert.ToString(expected, CultureInfo.InvariantCulture), out var expectedBoolean))
        {
            return actualBoolean == expectedBoolean;
        }

        if (TryUtcInstant(actual, out var actualInstant) &&
            TryUtcInstant(expected, out var expectedInstant))
        {
            return actualInstant == expectedInstant;
        }

        return string.Equals(
            Convert.ToString(actual, CultureInfo.InvariantCulture),
            Convert.ToString(expected, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryUtcInstant(object value, out DateTimeOffset result)
    {
        switch (value)
        {
            case DateTimeOffset offset:
                result = offset.ToUniversalTime();
                return true;
            case DateTime dateTime:
                result = new DateTimeOffset(
                        dateTime.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                            : dateTime)
                    .ToUniversalTime();
                return true;
            case string text when text.Contains('T', StringComparison.Ordinal) &&
                                  DateTimeOffset.TryParse(
                                      text,
                                      CultureInfo.InvariantCulture,
                                      DateTimeStyles.AllowWhiteSpaces |
                                      DateTimeStyles.AssumeUniversal |
                                      DateTimeStyles.AdjustToUniversal,
                                      out var parsed):
                result = parsed;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static int Compare(object? actual, object? expected)
    {
        if (!TryDecimal(actual, out var actualDecimal) || !TryDecimal(expected, out var expectedDecimal))
        {
            throw new RuntimeBundleTestException($"Cannot compare '{actual}' and '{expected}' numerically.");
        }

        return actualDecimal.CompareTo(expectedDecimal);
    }

    private static bool TryDecimal(object? value, out decimal result) =>
        decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out result);

    private static bool IsV3(BundleAcceptanceTestDocument document) =>
        string.Equals(document.Schema, BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal);

    internal static void ValidateV3RecordReferences(
        BundleAcceptanceTestDocument test,
        IEnumerable<string> seedRefs)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seedRef in seedRefs)
        {
            if (string.IsNullOrWhiteSpace(seedRef) || !available.Add(seedRef))
            {
                throw new RuntimeBundleTestException(
                    $"V3 acceptance test '{test.Test.Key}' contains an invalid or duplicate seed record ref '{seedRef}'.");
            }
        }

        foreach (var step in test.Steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Record?.Ref) && !available.Contains(step.Record.Ref))
            {
                throw new RuntimeBundleTestException(
                    $"V3 acceptance step '{step.Id}' references record ref '{step.Record.Ref}' outside its own seed and preceding successful producers.");
            }

            if (BundleAcceptanceExpectedOutcomeContract.IsRecordProducer(step) &&
                BundleAcceptanceExpectedOutcomeContract.IsSuccess(step))
            {
                if (string.IsNullOrWhiteSpace(step.Id) || !available.Add(step.Id))
                {
                    throw new RuntimeBundleTestException(
                        $"V3 acceptance producer step '{step.Id}' collides with its seed or a preceding producer record ref.");
                }
            }
        }

        foreach (var assertion in test.Assertions)
        {
            if (!string.IsNullOrWhiteSpace(assertion.Record?.Ref) && !available.Contains(assertion.Record.Ref) ||
                !string.IsNullOrWhiteSpace(assertion.ValueRef) && !available.Contains(assertion.ValueRef) ||
                !string.IsNullOrWhiteSpace(assertion.Where?.ValueRef) && !available.Contains(assertion.Where.ValueRef))
            {
                throw new RuntimeBundleTestException(
                    $"V3 acceptance assertion '{assertion.Id}' references a record outside its own seed and producers.");
            }
        }
    }

    private static RuntimeUserContext UserForRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new RuntimeBundleTestException("Acceptance test steps must declare an explicit as_role.");
        }

        var normalized = role.Trim().ToLowerInvariant();
        if (RuntimeSystemRoleIds.IsReserved(normalized))
        {
            throw new RuntimeBundleTestException(
                $"Acceptance test steps cannot use reserved system role '{RuntimeSystemRoleIds.TenantOwner}'.");
        }

        return new RuntimeUserContext(SyntheticUserId(normalized), $"{normalized}@bundle-tests.example", normalized)
        {
            Roles = [normalized]
        };
    }

    private static RuntimeUserContext SystemTestUser() =>
        new(SyntheticUserId(RuntimeSystemRoleIds.TenantOwner), "system@bundle-tests.invalid", RuntimeSystemRoleIds.TenantOwner)
        {
            Roles = [RuntimeSystemRoleIds.TenantOwner]
        };

    private static long SyntheticUserId(string role)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(role));
        var positive = (long)(BitConverter.ToUInt64(hash, 0) & int.MaxValue);
        return -Math.Max(1L, positive);
    }

    private async Task StoreResultAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestRunResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenProvisioningConnectionAsync(tenant, cancellationToken);
        await EnsureTestResultsTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into _runtime.test_results (bundle_version, bundle_hash, test_suite_hash, seed_hash, status, total, passed, failed, results)
            values (@bundleVersion, @bundleHash, @testSuiteHash, @seedHash, @status, @total, @passed, @failed, @results)
            """;
        command.Parameters.AddWithValue("bundleVersion", result.BundleVersion);
        command.Parameters.AddWithValue("bundleHash", result.BundleHash);
        command.Parameters.AddWithValue("testSuiteHash", result.TestSuiteHash);
        command.Parameters.AddWithValue("seedHash", result.SeedHash);
        command.Parameters.AddWithValue("status", result.Status);
        command.Parameters.AddWithValue("total", result.Total);
        command.Parameters.AddWithValue("passed", result.Passed);
        command.Parameters.AddWithValue("failed", result.Failed);
        command.Parameters.Add("results", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(result.Results, JsonOptions);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(string Version, string Hash)?> ReadActiveBundleBindingAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select bundle_version, bundle_hash
            from _runtime.active_bundle
            where id = true
            """;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return (reader.GetString(0), reader.GetString(1));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    private async Task ValidateActiveV3ActorRolesAsync(
        ResolvedTenantEnvironment tenant,
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select id from _runtime.role_definitions";
        var activeRoleIds = new List<string>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                activeRoleIds.Add(reader.GetString(0));
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new RuntimeBundleTestException(
                "V3 acceptance actors require active compiled bundle role definitions.");
        }

        var unsupported = BundleAcceptanceActorRolePolicy.FindUnsupportedReason(tests, activeRoleIds);
        if (unsupported is not null)
        {
            throw new RuntimeBundleTestException(unsupported);
        }
    }

    private static void ValidateV3ActorPrincipalIds(
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        string suiteHash)
    {
        var principals = new Dictionary<long, string>();
        var systemPrincipalId = SystemTestUser().UserId;
        foreach (var test in tests.Where(IsV3))
        {
            foreach (var actor in test.Actors)
            {
                var principalId = BundleAcceptanceActorPrincipalId.Derive(
                    suiteHash,
                    test.Test.Key,
                    actor.Id);
                var identity = $"{test.Test.Key}:{actor.Id}";
                if (principalId == systemPrincipalId)
                {
                    throw new RuntimeBundleTestException(
                        $"Acceptance actor '{identity}' collides with the reserved test-system principal.");
                }

                if (principals.TryGetValue(principalId, out var existingIdentity))
                {
                    throw new RuntimeBundleTestException(
                        $"Acceptance actors '{existingIdentity}' and '{identity}' have a principal identity collision.");
                }

                principals[principalId] = identity;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, RuntimeUserContext>> PersistV3ActorsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeBundleTestDatabaseLease lease,
        BundleAcceptanceTestDocument test,
        string suiteHash,
        CancellationToken cancellationToken)
    {
        var actorRows = new List<(string ActorId, long UserId, string Email, string Role)>();
        foreach (var actor in test.Actors)
        {
            if (actor.Roles.Count != 1)
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance test '{test.Test.Key}' actor '{actor.Id}' must have exactly one durable role.");
            }

            var userId = BundleAcceptanceActorPrincipalId.Derive(suiteHash, test.Test.Key, actor.Id);
            actorRows.Add((
                actor.Id,
                userId,
                $"acceptance-{Math.Abs(userId).ToString(CultureInfo.InvariantCulture)}@bundle-tests.invalid",
                actor.Roles[0]));
        }

        await using (var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await using (var ownershipCommand = new NpgsqlCommand(
                                 """
                                 select exists (
                                   select 1
                                   from _runtime.acceptance_test_ownership
                                   where id = true
                                     and run_id = @runId
                                     and database_name = current_database())
                                 """,
                                 lease.Connection,
                                 transaction))
                {
                    ownershipCommand.Parameters.AddWithValue("runId", lease.RunId);
                    if (await ownershipCommand.ExecuteScalarAsync(cancellationToken) is not true)
                    {
                        throw new RuntimeBundleTestException(
                            "Acceptance ownership marker is missing or does not match this run.");
                    }
                }

                foreach (var actor in actorRows)
                {
                    await using (var userCommand = new NpgsqlCommand(
                                     """
                                     insert into _runtime.runtime_users (
                                       id,
                                       email,
                                       display_name,
                                       password_hash,
                                       role,
                                       status,
                                       activated_at)
                                     select
                                       @userId,
                                       @email,
                                       @displayName,
                                       null,
                                       @role,
                                       'active',
                                       now()
                                     where exists (
                                       select 1
                                       from _runtime.role_definitions
                                       where id = @role)
                                       and exists (
                                         select 1
                                         from _runtime.runtime_roles
                                         where id = @role)
                                     returning id
                                     """,
                                     lease.Connection,
                                     transaction))
                    {
                        userCommand.Parameters.AddWithValue("userId", actor.UserId);
                        userCommand.Parameters.AddWithValue("email", actor.Email);
                        userCommand.Parameters.AddWithValue(
                            "displayName",
                            $"Acceptance actor {test.Test.Key}/{actor.ActorId}");
                        userCommand.Parameters.AddWithValue("role", actor.Role);
                        if (await userCommand.ExecuteScalarAsync(cancellationToken) is null)
                        {
                            throw new RuntimeBundleTestException(
                                $"Acceptance actor '{test.Test.Key}:{actor.ActorId}' role '{actor.Role}' is not active in runtime identity metadata.");
                        }
                    }

                    await using (var roleCommand = new NpgsqlCommand(
                                     """
                                     insert into _runtime.runtime_user_roles (user_id, role_id)
                                     values (@userId, @role)
                                     """,
                                     lease.Connection,
                                     transaction))
                    {
                        roleCommand.Parameters.AddWithValue("userId", actor.UserId);
                        roleCommand.Parameters.AddWithValue("role", actor.Role);
                        await roleCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using var markerCommand = new NpgsqlCommand(
                        """
                        insert into _runtime.acceptance_test_actor_ownership (
                          run_id,
                          test_key,
                          actor_id,
                          runtime_user_id,
                          email,
                          role_id)
                        values (@runId, @testKey, @actorId, @userId, @email, @role)
                        """,
                        lease.Connection,
                        transaction);
                    markerCommand.Parameters.AddWithValue("runId", lease.RunId);
                    markerCommand.Parameters.AddWithValue("testKey", test.Test.Key);
                    markerCommand.Parameters.AddWithValue("actorId", actor.ActorId);
                    markerCommand.Parameters.AddWithValue("userId", actor.UserId);
                    markerCommand.Parameters.AddWithValue("email", actor.Email);
                    markerCommand.Parameters.AddWithValue("role", actor.Role);
                    await markerCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RuntimeBundleTestException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance actor persistence failed: {exception.Message}");
            }
        }

        var persisted = new Dictionary<string, RuntimeUserContext>(StringComparer.Ordinal);
        foreach (var actor in actorRows)
        {
            var user = await users.GetActiveUserContextAsync(tenant, actor.UserId, cancellationToken);
            if (user is null ||
                !string.Equals(user.Role, actor.Role, StringComparison.Ordinal) ||
                user.Roles.Count != 1 ||
                !string.Equals(user.Roles[0], actor.Role, StringComparison.Ordinal))
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance actor '{test.Test.Key}:{actor.ActorId}' could not be reloaded with its exact durable role.");
            }

            persisted.Add(actor.ActorId, user);
        }

        return persisted;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<NpgsqlConnection> OpenProvisioningConnectionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
            tenant,
            TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<RuntimeBundleTestDatabaseLease> AcquireTestDatabaseLeaseAsync(
        ResolvedTenantEnvironment tenant,
        string bundleVersion,
        string bundleHash,
        string suiteHash,
        CancellationToken cancellationToken)
    {
        var connection = await OpenProvisioningConnectionAsync(tenant, cancellationToken);
        var runId = Guid.NewGuid();
        var locked = false;
        try
        {
            await using (var lockCommand = connection.CreateCommand())
            {
                lockCommand.CommandText =
                    "select pg_try_advisory_lock(1886545253, hashtext(current_database()))";
                locked = await lockCommand.ExecuteScalarAsync(cancellationToken) is true;
            }

            if (!locked)
            {
                throw new RuntimeBundleTestException(
                    "Acceptance database is already leased by another test run.");
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var activeCommand = new NpgsqlCommand(
                             """
                             select exists (
                               select 1
                               from _runtime.active_bundle
                               where id = true
                                 and bundle_version = @bundleVersion
                                 and bundle_hash = @bundleHash)
                             """,
                             connection,
                             transaction))
            {
                activeCommand.Parameters.AddWithValue("bundleVersion", bundleVersion);
                activeCommand.Parameters.AddWithValue("bundleHash", bundleHash);
                if (await activeCommand.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    throw new RuntimeBundleTestException(
                        "Acceptance database active bundle changed before lease acquisition.");
                }
            }

            await using (var command = new NpgsqlCommand(
                             """
                             create table if not exists _runtime.acceptance_test_ownership (
                               id boolean primary key default true check (id),
                               run_id uuid not null,
                               database_name text not null,
                               bundle_version varchar(80) not null,
                               bundle_hash varchar(128) not null,
                               test_suite_hash varchar(64) not null,
                               claimed_at timestamptz not null default now()
                             );

                             create table if not exists _runtime.acceptance_test_actor_ownership (
                               run_id uuid not null,
                               test_key varchar(128) not null,
                               actor_id varchar(128) not null,
                               runtime_user_id bigint not null,
                               email varchar(320) not null,
                               role_id varchar(80) not null,
                               primary key (run_id, runtime_user_id),
                               unique (run_id, test_key, actor_id)
                             );

                             alter table _runtime.acceptance_test_actor_ownership
                               alter column test_key type varchar(128),
                               alter column actor_id type varchar(128);

                             do $acceptance_pristine$
                             declare
                               item record;
                               populated boolean;
                             begin
                               if exists (select 1 from _runtime.acceptance_test_ownership) then
                                 raise exception 'Acceptance database has a stale ownership marker.';
                               end if;

                               if exists (select 1 from _runtime.acceptance_test_actor_ownership) then
                                 raise exception 'Acceptance database has stale actor ownership markers.';
                               end if;

                               for item in
                                 select schemaname, tablename
                                 from pg_tables
                                 where schemaname in ('app', 'integration')
                                 order by schemaname, tablename
                               loop
                                 execute format(
                                   'select exists (select 1 from %I.%I limit 1)',
                                   item.schemaname,
                                   item.tablename)
                                 into populated;
                                 if populated then
                                   raise exception 'Acceptance tests require pristine app/integration tables; %.% contains data.',
                                     item.schemaname,
                                     item.tablename;
                                 end if;
                               end loop;

                               if exists (select 1 from _runtime.runtime_users) or
                                  exists (select 1 from _runtime.runtime_user_roles) or
                                  exists (select 1 from _runtime.runtime_user_links) or
                                  exists (select 1 from _runtime.activation_tokens) or
                                  exists (select 1 from _runtime.workflow_instances) or
                                  exists (select 1 from _runtime.workflow_tasks) or
                                  exists (select 1 from _runtime.workflow_events) or
                                  exists (select 1 from _runtime.notification_outbox) or
                                  -- A durable result closes this disposable test database.
                                  -- Broker retries consume a passing result or require the
                                  -- explicit staging reset lifecycle; direct reruns fail closed.
                                  exists (select 1 from _runtime.test_results) then
                                 raise exception 'Acceptance tests require a pristine test-owned staging database.';
                               end if;
                             end
                             $acceptance_pristine$;

                             insert into _runtime.acceptance_test_ownership (
                               id,
                               run_id,
                               database_name,
                               bundle_version,
                               bundle_hash,
                               test_suite_hash)
                             values (true, @runId, current_database(), @bundleVersion, @bundleHash, @suiteHash)
                             """,
                             connection,
                             transaction))
            {
                command.Parameters.AddWithValue("runId", runId);
                command.Parameters.AddWithValue("bundleVersion", bundleVersion);
                command.Parameters.AddWithValue("bundleHash", bundleHash);
                command.Parameters.AddWithValue("suiteHash", suiteHash);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            long auditBoundary;
            await using (var auditCommand = new NpgsqlCommand(
                             "select coalesce(max(id), 0) from audit.events",
                             connection,
                             transaction))
            {
                auditBoundary = Convert.ToInt64(
                    await auditCommand.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(cancellationToken);
            return new RuntimeBundleTestDatabaseLease(connection, runId, auditBoundary);
        }
        catch (OperationCanceledException)
        {
            if (locked)
            {
                await TryUnlockTestDatabaseAsync(connection);
            }

            await connection.DisposeAsync();
            throw;
        }
        catch (RuntimeBundleTestException)
        {
            if (locked)
            {
                await TryUnlockTestDatabaseAsync(connection);
            }

            await connection.DisposeAsync();
            throw;
        }
        catch (Exception exception)
        {
            if (locked)
            {
                await TryUnlockTestDatabaseAsync(connection);
            }

            await connection.DisposeAsync();
            throw new RuntimeBundleTestException(
                $"Acceptance database ownership precondition failed: {exception.Message}");
        }
    }

    private static async Task CleanupTestOwnedDataAsync(
        RuntimeBundleTestDatabaseLease lease,
        bool deleteTestAudit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var ownershipCommand = new NpgsqlCommand(
                             """
                             select exists (
                               select 1
                               from _runtime.acceptance_test_ownership
                               where id = true
                                 and run_id = @runId
                                 and database_name = current_database())
                             """,
                             lease.Connection,
                             transaction))
            {
                ownershipCommand.Parameters.AddWithValue("runId", lease.RunId);
                if (await ownershipCommand.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    throw new RuntimeBundleTestException(
                        "Acceptance ownership marker is missing or does not match this run.");
                }
            }

            await using var command = new NpgsqlCommand(
                """
                select set_config('erpgenerator.acceptance_run_id', cast(@runId as text), true);

                do $acceptance_actor_cleanup$
                declare
                  expected_count bigint;
                  deleted_count bigint;
                begin
                  if exists (
                    select 1
                    from _runtime.acceptance_test_actor_ownership
                    where run_id <> current_setting('erpgenerator.acceptance_run_id')::uuid) then
                    raise exception 'Acceptance actor ownership belongs to another run.';
                  end if;

                  if exists (
                    select 1
                    from _runtime.acceptance_test_actor_ownership owned
                    left join _runtime.runtime_users users
                      on users.id = owned.runtime_user_id
                     and users.email = owned.email
                    where owned.run_id = current_setting('erpgenerator.acceptance_run_id')::uuid
                      and users.id is null) then
                    raise exception 'Acceptance actor identity no longer matches its ownership marker.';
                  end if;

                  if exists (
                    select 1
                    from _runtime.runtime_users users
                    where not exists (
                      select 1
                      from _runtime.acceptance_test_actor_ownership owned
                      where owned.run_id = current_setting('erpgenerator.acceptance_run_id')::uuid
                        and owned.runtime_user_id = users.id
                        and owned.email = users.email)) then
                    raise exception 'Acceptance run created an unowned runtime user.';
                  end if;

                  select count(*)
                  into expected_count
                  from _runtime.acceptance_test_actor_ownership
                  where run_id = current_setting('erpgenerator.acceptance_run_id')::uuid;

                  delete from _runtime.runtime_users users
                  using _runtime.acceptance_test_actor_ownership owned
                  where owned.run_id = current_setting('erpgenerator.acceptance_run_id')::uuid
                    and users.id = owned.runtime_user_id
                    and users.email = owned.email;
                  get diagnostics deleted_count = row_count;

                  if deleted_count <> expected_count then
                    raise exception 'Acceptance actor cleanup did not delete the exact owned identities.';
                  end if;

                  delete from _runtime.acceptance_test_actor_ownership
                  where run_id = current_setting('erpgenerator.acceptance_run_id')::uuid;
                end
                $acceptance_actor_cleanup$;

                do $acceptance_cleanup$
                declare
                  mutable_tables text;
                begin
                  select string_agg(
                           format('%I.%I', schemaname, tablename),
                           ', ' order by schemaname, tablename)
                  into mutable_tables
                  from pg_tables
                  where schemaname in ('app', 'integration');

                  if mutable_tables is not null then
                    execute 'truncate table ' || mutable_tables || ' cascade';
                  end if;
                end
                $acceptance_cleanup$;

                delete from _runtime.notification_outbox;
                delete from _runtime.workflow_events;
                delete from _runtime.workflow_tasks;
                delete from _runtime.scheduled_jobs;
                delete from _runtime.workflow_instances;
                delete from audit.events
                where @deleteTestAudit and id > @auditBoundary
                """,
                lease.Connection,
                transaction);
            command.Parameters.AddWithValue("runId", lease.RunId);
            command.Parameters.AddWithValue("deleteTestAudit", deleteTestAudit);
            command.Parameters.AddWithValue("auditBoundary", lease.AuditBoundary);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RuntimeBundleTestException(
                $"Acceptance test-owned data cleanup failed: {exception.Message}");
        }
    }

    private static async Task ReleaseTestDatabaseLeaseAsync(
        RuntimeBundleTestDatabaseLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = lease.Connection.CreateCommand();
            command.CommandText =
                "delete from _runtime.acceptance_test_ownership where id = true and run_id = @runId";
            command.Parameters.AddWithValue("runId", lease.RunId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await TryUnlockTestDatabaseAsync(lease.Connection);
            await lease.Connection.DisposeAsync();
        }
    }

    private static async Task TryUnlockTestDatabaseAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "select pg_advisory_unlock(1886545253, hashtext(current_database()))";
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch
        {
            // Closing the session releases the advisory lock even when the
            // explicit unlock cannot be observed during failure cleanup.
        }
    }

    internal static async Task EnsureTestResultsTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            create schema if not exists _runtime;
            create table if not exists _runtime.test_results (
              id bigserial primary key,
              bundle_version varchar(80) not null,
              bundle_hash varchar(128) not null,
              test_suite_hash varchar(64) not null,
              seed_hash varchar(64) not null default '',
              status varchar(40) not null,
              total integer not null,
              passed integer not null,
              failed integer not null,
              results jsonb not null,
              created_at timestamptz not null default now()
            );
            alter table _runtime.test_results
              add column if not exists bundle_hash varchar(128);
            alter table _runtime.test_results
              add column if not exists test_suite_hash varchar(64)
            ;
            alter table _runtime.test_results
              add column if not exists seed_hash varchar(64) not null default ''
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class RuntimeBundleTestContext
    {
        private readonly Dictionary<string, RuntimeSeedRecordRef> seedRefs =
            new(StringComparer.Ordinal);

        public RuntimeBundleTestContext(IReadOnlyDictionary<string, RuntimeSeedRecordRef> initialRefs)
        {
            foreach (var (key, value) in initialRefs)
            {
                if (string.IsNullOrWhiteSpace(key) || !Refs.TryAdd(key, value) ||
                    !seedRefs.TryAdd(key, value))
                {
                    throw new RuntimeBundleTestException(
                        $"Acceptance seed contains an invalid or duplicate record ref '{key}'.");
                }
            }
        }

        public Dictionary<string, RuntimeSeedRecordRef> Refs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RuntimeSeedRecordRef> SuccessfulProducedRecords { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RuntimeWorkflowStartResult> WorkflowStarts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RuntimeWorkflowTaskResult> WorkflowTasks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RuntimeReportResult> Reports { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RuntimeBundleTestScheduleRun> ScheduleRuns { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RuntimeBundleTestActorExecution> StepActors { get; } = new(StringComparer.Ordinal);

        private Dictionary<string, BundleAcceptanceStep> StepsById { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, RuntimeBundleTestAssertionEvidence> AssertionEvidence { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> AuditEventBoundariesByStep { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> NotificationBoundariesByStep { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> NotificationCheckpointsByStep { get; } = new(StringComparer.Ordinal);

        private Dictionary<string, RuntimeBundleTestActorExecution> actors = new(StringComparer.Ordinal);

        public bool IsV3 { get; private set; }

        public Guid? AcceptanceRunId { get; set; }

        public NpgsqlConnection? AcceptanceFenceOwnerConnection { get; set; }

        public long AuditEventBoundaryId { get; set; }

        public void RecordSuccessfulProducer(RuntimeSeedRecordRef reference)
        {
            if (string.IsNullOrWhiteSpace(reference.Ref) ||
                Refs.ContainsKey(reference.Ref) ||
                SuccessfulProducedRecords.ContainsKey(reference.Ref))
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance producer record ref '{reference.Ref}' collides with an existing seed or producer ref.");
            }

            Refs.Add(reference.Ref, reference);
            SuccessfulProducedRecords.Add(reference.Ref, reference);
        }

        public void ReferenceSeedRecord(string producerRef, string seedRef, string entityId)
        {
            if (string.IsNullOrWhiteSpace(seedRef) ||
                !seedRefs.TryGetValue(seedRef, out var seed) ||
                !seed.Entity.Equals(entityId, StringComparison.Ordinal))
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance reference_seed_record producer '{producerRef}' must resolve one exact record from its own hash-bound seed for entity '{entityId}'.");
            }

            RecordSuccessfulProducer(seed with { Ref = producerRef });
        }

        public void RecordScheduleRun(RuntimeBundleTestScheduleRun run)
        {
            var previous = ScheduleRuns.Values.LastOrDefault(candidate =>
                candidate.ScheduleId.Equals(run.ScheduleId, StringComparison.Ordinal) &&
                candidate.EntityId.Equals(run.EntityId, StringComparison.Ordinal) &&
                candidate.RecordId == run.RecordId);
            if (previous is not null &&
                (previous.Job.Id != run.Job.Id ||
                 !previous.Job.ScheduleDefinitionHash.Equals(run.Job.ScheduleDefinitionHash, StringComparison.Ordinal) ||
                 previous.Job.WorkflowInstanceId is not null &&
                 previous.Job.WorkflowInstanceId != run.Job.WorkflowInstanceId))
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance run_due_schedule step '{run.StepId}' did not preserve one-shot job and workflow identity across repeated cycles.");
            }

            if (!ScheduleRuns.TryAdd(run.StepId, run))
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance run_due_schedule step '{run.StepId}' produced duplicate scheduler evidence.");
            }
        }

        public void RecordScheduledWorkflowStart(
            string stepId,
            RuntimeWorkflowStartResult start)
        {
            if (!WorkflowStarts.TryAdd(stepId, start))
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance run_due_schedule step '{stepId}' produced duplicate workflow-ref evidence.");
            }
        }

        public bool IsExactSuccessfulProducedRecordReference(
            RuntimeSeedRecordRef reference,
            string entityId) =>
            RuntimeBundleTestRunner.IsExactSuccessfulProducedRecordReference(
                SuccessfulProducedRecords,
                reference,
                entityId);

        public void ConfigureActors(
            BundleAcceptanceTestDocument test,
            IReadOnlyDictionary<string, RuntimeUserContext>? persistedActors)
        {
            IsV3 = RuntimeBundleTestRunner.IsV3(test);
            StepsById = test.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
            StepActors.Clear();
            AssertionEvidence.Clear();
            AuditEventBoundariesByStep.Clear();
            NotificationBoundariesByStep.Clear();
            NotificationCheckpointsByStep.Clear();
            if (!IsV3)
            {
                actors = new Dictionary<string, RuntimeBundleTestActorExecution>(StringComparer.Ordinal);
                return;
            }

            if (persistedActors is null || persistedActors.Count != test.Actors.Count)
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance test '{test.Test.Key}' actors were not durably provisioned.");
            }

            actors = test.Actors.ToDictionary(
                actor => actor.Id,
                actor =>
                {
                    if (!persistedActors.TryGetValue(actor.Id, out var user) ||
                        actor.Roles.Count != 1 ||
                        user.Roles.Count != 1 ||
                        !string.Equals(user.Role, actor.Roles[0], StringComparison.Ordinal) ||
                        !string.Equals(user.Roles[0], actor.Roles[0], StringComparison.Ordinal))
                    {
                        throw new RuntimeBundleTestException(
                            $"Acceptance test '{test.Test.Key}' actor '{actor.Id}' does not match its exact durable role.");
                    }

                    return new RuntimeBundleTestActorExecution(actor.Id, user);
                },
                StringComparer.Ordinal);

            if (actors.Values.Select(actor => actor.User.UserId).Distinct().Count() != actors.Count)
            {
                throw new RuntimeBundleTestException(
                    $"Acceptance test '{test.Test.Key}' actor principal identity collision.");
            }
        }

        public bool TryGetStep(string id, out BundleAcceptanceStep step) =>
            StepsById.TryGetValue(id, out step!);

        public RuntimeBundleTestActorExecution ResolveActor(BundleAcceptanceStep step)
        {
            RuntimeBundleTestActorExecution actor;
            if (IsV3)
            {
                if (!actors.TryGetValue(step.AsActor, out actor!))
                {
                    throw new RuntimeBundleTestException(
                        $"Acceptance step '{step.Id}' references unknown actor '{step.AsActor}'.");
                }
            }
            else
            {
                actor = new RuntimeBundleTestActorExecution(step.AsRole, UserForRole(step.AsRole));
            }

            StepActors[step.Id] = actor;
            return actor;
        }

        public IReadOnlyDictionary<string, object?> ResolveContractValues(
            IReadOnlyDictionary<string, object?> values) => values.ToDictionary(
            pair => pair.Key,
            pair => ResolveContractValue(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        public object? ResolveContractValue(object? value)
        {
            var normalized = RuntimeSeedRunner.NormalizeYamlValue(value);
            if (!IsV3)
            {
                return normalized;
            }

            return ResolveV3ContractValue(normalized);
        }

        private object? ResolveV3ContractValue(object? value)
        {
            if (value is string text && BundleAcceptanceActorPrincipalToken.IsCandidate(text))
            {
                if (!BundleAcceptanceActorPrincipalToken.TryParse(text, out var actorId) ||
                    !actors.TryGetValue(actorId, out var actor))
                {
                    throw new RuntimeBundleTestException(
                        $"V3 acceptance value contains malformed or unknown actor principal token '{text}'.");
                }

                return actor.User.UserId;
            }

            if (value is JsonElement element)
            {
                return ResolveV3JsonElement(element);
            }

            if (value is IReadOnlyDictionary<string, object?> map)
            {
                return map.ToDictionary(
                    pair => pair.Key,
                    pair => ResolveV3ContractValue(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (value is IEnumerable<object?> list && value is not string)
            {
                return list.Select(ResolveV3ContractValue).ToArray();
            }

            return value;
        }

        private object? ResolveV3JsonElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ResolveV3JsonElement(property.Value),
                StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(ResolveV3JsonElement).ToArray(),
            JsonValueKind.String => ResolveV3ContractValue(element.GetString()),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.Clone()
        };
    }

    private sealed record RuntimeBundleTestActorExecution(
        string ActorId,
        RuntimeUserContext User);

    private sealed record RuntimeBundleTestDatabaseLease(
        NpgsqlConnection Connection,
        Guid RunId,
        long AuditBoundary);

    private sealed record RuntimeBundleTestScheduleAuthority(
        string WorkflowId,
        string DefinitionHash);

    private sealed record RuntimeBundleTestScheduleRun(
        string StepId,
        string ScheduleId,
        string ScheduleTime,
        string EntityId,
        string RecordRef,
        long RecordId,
        string WorkflowId,
        DateTimeOffset ObservedAt,
        RuntimeScheduledJob Job,
        RuntimeScheduleCycleResult Cycle);

    private sealed record RuntimeBundleTestAssertionEvidence
    {
        public bool HasExpectedValue { get; init; } = true;

        public object? ExpectedValue { get; init; }

        public string ExpectedValueKind { get; init; } = string.Empty;

        public bool HasActualValue { get; init; } = true;

        public object? ActualValue { get; init; }

        public string ActualValueKind { get; init; } = string.Empty;

        public long? WorkflowInstanceId { get; init; }

        public string WorkflowStatus { get; init; } = string.Empty;

        public IReadOnlyList<long> TaskIds { get; init; } = [];

        public IReadOnlyList<string> TaskSteps { get; init; } = [];

        public IReadOnlyList<string> TaskStatuses { get; init; } = [];

        public int? RowCount { get; init; }

        public IReadOnlyList<object?> ReportMetricValues { get; init; } = [];

        public string ReportMetricValueKind { get; init; } = string.Empty;

        public string ReportOutputHash { get; init; } = string.Empty;

        public bool? ReportTruncated { get; init; }

        public int? ReportAppliedLimit { get; init; }

        public string DashboardWidgetReport { get; init; } = string.Empty;

        public string DashboardWidgetMetric { get; init; } = string.Empty;

        public IReadOnlyList<long> AuditEventIds { get; init; } = [];

        public IReadOnlyList<long> NotificationIds { get; init; } = [];

        public IReadOnlyList<RuntimeBundleTestNotificationObservation> Notifications { get; init; } = [];
    }
}

public sealed record RuntimeBundleTestRunResult(
    string BundleVersion,
    int Total,
    int Passed,
    int Failed,
    string Status,
    IReadOnlyList<RuntimeBundleTestResult> Results,
    string BundleHash,
    string TestSuiteHash)
{
    public string SeedHash { get; init; } = string.Empty;
}

public sealed record RuntimeBundleTestResult(
    string Key,
    string Label,
    string SourcePath,
    string Status,
    IReadOnlyList<string> Failures,
    string ContractHash,
    string Origin,
    int SuiteIndex)
{
    public IReadOnlyList<RuntimeBundleTestStepObservation> StepObservations { get; init; } = [];

    public IReadOnlyList<RuntimeBundleTestAssertionObservation> AssertionObservations { get; init; } = [];
}

public sealed class RuntimeBundleTestException(string message) : Exception(message);
