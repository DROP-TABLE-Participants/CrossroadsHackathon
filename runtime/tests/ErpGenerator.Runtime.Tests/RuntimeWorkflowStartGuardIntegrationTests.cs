using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeWorkflowStartGuardIntegrationTests
{
    private const string FixedGuardMessage = "Workflow start requirements are not satisfied.";
    private const string RawSensitiveError = "customer-secret: pending onboarding task details";

    [PostgresFact]
    public async Task Guards_are_exact_internal_fail_closed_and_rollback_every_workflow_write()
    {
        await WithRuntimeAsync("workflow_guard", async runtime =>
        {
            var blocked = await CreateSubscriptionAsync(runtime, "Blocked");
            var blockedId = Convert.ToInt64(blocked["id"]);
            await CreateTaskAsync(runtime, blockedId, "pending", "Blocked pending");

            var ready = await CreateSubscriptionAsync(runtime, "Ready");
            var readyId = Convert.ToInt64(ready["id"]);
            await CreateTaskAsync(runtime, readyId, "done", "Ready complete");

            var unrelated = await CreateSubscriptionAsync(runtime, "Unrelated");
            await CreateTaskAsync(
                runtime,
                Convert.ToInt64(unrelated["id"]),
                "pending",
                "Unrelated pending");

            var rejected = await FluentActions.Awaiting(() => runtime.Workflows.StartActionAsync(
                    runtime.Tenant,
                    Starter(),
                    "subscriptions",
                    blockedId,
                    "activate"))
                .Should().ThrowAsync<RuntimeWorkflowStartGuardRejectedException>();
            rejected.Which.GuardId.Should().Be("no_pending_tasks");
            rejected.Which.Message.Should().Be(FixedGuardMessage).And.NotContain("customer-secret");
            (await CountInstancesAsync(runtime, "subscription_activation", "subscription", blockedId))
                .Should().Be(0);
            (await CountWorkflowEventsAsync(runtime, "subscription_activation", blockedId))
                .Should().Be(0);

            var started = await runtime.Workflows.StartActionAsync(
                runtime.Tenant,
                Starter(),
                "subscriptions",
                readyId,
                "activate");
            started.Instance.WorkflowId.Should().Be("subscription_activation");
            (await runtime.Records.GetRecordAsync(runtime.Tenant, "subscriptions", readyId))!["status"]
                .Should().Be("activated", "an unrelated pending task must not affect this exact subscription");
            (await CountInstancesAsync(runtime, "subscription_activation", "subscription", readyId))
                .Should().Be(1);
            (await runtime.Workflows.ListRecordActionsAsync(
                    runtime.Tenant,
                    Starter(),
                    "subscriptions",
                    readyId))
                .Should().BeEmpty("the workflow moved its configured lifecycle field away from the initial state");

            var request = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                runtime.Owner,
                "activation_requests",
                RuntimeJson.JsonObject(("name", "Missing optional parent")));
            var requestId = Convert.ToInt64(request["id"]);
            var missingCorrelation = await FluentActions.Awaiting(() => runtime.Workflows.StartActionAsync(
                    runtime.Tenant,
                    Starter(),
                    "activation_requests",
                    requestId,
                    "activate_from_subscription"))
                .Should().ThrowAsync<RuntimeWorkflowStartGuardRejectedException>();
            missingCorrelation.Which.GuardId.Should().Be(
                "z_parent_exists",
                "canonical locking must not change the authored first-failing guard target");
            missingCorrelation.Which.Message.Should().Be(FixedGuardMessage);
            (await CountInstancesAsync(runtime, "activation_request_start", "activation_request", requestId))
                .Should().Be(0);

            var rollback = await CreateSubscriptionAsync(runtime, "Rollback");
            var rollbackId = Convert.ToInt64(rollback["id"]);
            await CreateTaskAsync(runtime, rollbackId, "done", "Rollback complete");
            await CorruptFirstWorkflowStepAsync(runtime, "subscription_activation");

            await FluentActions.Awaiting(() => runtime.Workflows.StartActionAsync(
                    runtime.Tenant,
                    Starter(),
                    "subscriptions",
                    rollbackId,
                    "activate"))
                .Should().ThrowAsync<RuntimeWorkflowValidationException>();
            (await CountInstancesAsync(runtime, "subscription_activation", "subscription", rollbackId))
                .Should().Be(0, "the instance insert must roll back with a later workflow-step failure");
            (await CountWorkflowEventsAsync(runtime, "subscription_activation", rollbackId))
                .Should().Be(0, "workflow.started and all later events share the same transaction");
            (await runtime.Records.GetRecordAsync(runtime.Tenant, "subscriptions", rollbackId))!["status"]
                .Should().Be("draft");
        });
    }

    [PostgresFact]
    public async Task High_cardinality_guards_saturate_without_failing_valid_count_comparisons()
    {
        await WithRuntimeAsync("workflow_guard_saturation", async runtime =>
        {
            var subscription = await CreateSubscriptionAsync(runtime, "High cardinality");
            var subscriptionId = Convert.ToInt64(subscription["id"]);
            await using (var connection = new NpgsqlConnection(
                             runtime.Factory.CreateConnectionString(runtime.Tenant)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    insert into app.onboarding_tasks (label, subscription_id, status)
                    select 'Completed task ' || ordinal, @subscriptionId, 'done'
                    from generate_series(1, @taskCount) ordinal
                    """;
                command.Parameters.AddWithValue("subscriptionId", subscriptionId);
                command.Parameters.AddWithValue(
                    "taskCount",
                    ErpWorkflowStartGuardLimits.MaxResultCount + 1);
                (await command.ExecuteNonQueryAsync()).Should().Be(
                    ErpWorkflowStartGuardLimits.MaxResultCount + 1);
            }

            var started = await runtime.Workflows.StartActionAsync(
                runtime.Tenant,
                Starter(),
                "subscriptions",
                subscriptionId,
                "activate");

            started.Instance.Status.Should().Be("completed");
            (await CountInstancesAsync(
                    runtime,
                    "subscription_activation",
                    "subscription",
                    subscriptionId))
                .Should().Be(1);
        });
    }

    [PostgresFact]
    public async Task Concurrent_starts_and_guarded_child_creates_serialize_without_orphan_instances()
    {
        await WithRuntimeAsync("workflow_guard_race", async runtime =>
        {
            var duplicate = await CreateSubscriptionAsync(runtime, "Duplicate start");
            var duplicateId = Convert.ToInt64(duplicate["id"]);
            await CreateTaskAsync(runtime, duplicateId, "done", "Duplicate complete");

            var duplicateStarts = await Task.WhenAll(
                    CaptureExceptionAsync(async () => await runtime.Workflows.StartActionAsync(
                        runtime.Tenant,
                        Starter(),
                        "subscriptions",
                        duplicateId,
                        "activate")),
                    CaptureExceptionAsync(async () => await runtime.Workflows.StartActionAsync(
                        runtime.Tenant,
                        Starter(),
                        "subscriptions",
                        duplicateId,
                        "activate")))
                .WaitAsync(TimeSpan.FromSeconds(10));
            duplicateStarts.Count(outcome => outcome is null).Should().Be(1);
            duplicateStarts.Count(outcome => outcome is RuntimeWorkflowActionNotAvailableException).Should().Be(1);
            (await CountInstancesAsync(runtime, "subscription_activation", "subscription", duplicateId))
                .Should().Be(1);

            var racing = await CreateSubscriptionAsync(runtime, "Racing child create");
            var racingId = Convert.ToInt64(racing["id"]);
            await CreateTaskAsync(runtime, racingId, "done", "Existing complete");
            await using var blocker = new NpgsqlConnection(runtime.Factory.CreateConnectionString(runtime.Tenant));
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var lockCommand = blocker.CreateCommand())
            {
                lockCommand.Transaction = blockerTransaction;
                lockCommand.CommandText = "select id from app.subscriptions where id = @id for update";
                lockCommand.Parameters.AddWithValue("id", racingId);
                Convert.ToInt64(await lockCommand.ExecuteScalarAsync()).Should().Be(racingId);
            }

            var startRace = CaptureExceptionAsync(async () => await runtime.Workflows.StartActionAsync(
                runtime.Tenant,
                Starter(),
                "subscriptions",
                racingId,
                "activate"));
            var createRace = CaptureExceptionAsync(async () => await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                runtime.Owner,
                "onboarding_tasks",
                RuntimeJson.JsonObject(
                    ("label", "Concurrent pending"),
                    ("subscription_id", racingId),
                    ("status", "pending"))));

            await Task.Delay(150);
            startRace.IsCompleted.Should().BeFalse();
            createRace.IsCompleted.Should().BeFalse();
            await blockerTransaction.CommitAsync();

            var outcomes = await Task.WhenAll(startRace, createRace).WaitAsync(TimeSpan.FromSeconds(10));
            outcomes.Count(outcome => outcome is null).Should().Be(1);
            outcomes.Where(outcome => outcome is not null &&
                                      outcome is not RuntimeWorkflowStartGuardRejectedException &&
                                      outcome is not RuntimeRelationCreateGuardRejectedException)
                .Should().BeEmpty();

            var current = await runtime.Records.GetRecordAsync(runtime.Tenant, "subscriptions", racingId);
            var pending = await CountPendingTasksAsync(runtime, racingId);
            var instances = await CountInstancesAsync(
                runtime,
                "subscription_activation",
                "subscription",
                racingId);
            if (current!["status"]?.ToString() == "activated")
            {
                pending.Should().Be(0);
                instances.Should().Be(1);
                startRace.Result.Should().BeNull();
                createRace.Result.Should().BeOfType<RuntimeRelationCreateGuardRejectedException>();
            }
            else
            {
                current["status"].Should().Be("draft");
                pending.Should().Be(1);
                instances.Should().Be(0, "a rejected start must not leave an orphan instance");
                startRace.Result.Should().BeOfType<RuntimeWorkflowStartGuardRejectedException>();
                createRace.Result.Should().BeNull();
            }
        });
    }

    [PostgresFact]
    public async Task Inverse_correlation_lock_orders_complete_through_bounded_deadlock_retry()
    {
        await WithRuntimeAsync("workflow_guard_deadlock", async runtime =>
        {
            var first = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                runtime.Owner,
                "nodes",
                RuntimeJson.JsonObject(("name", "First node")));
            var firstId = Convert.ToInt64(first["id"]);
            var second = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                runtime.Owner,
                "nodes",
                RuntimeJson.JsonObject(("name", "Second node"), ("peer_id", firstId)));
            var secondId = Convert.ToInt64(second["id"]);
            await runtime.Records.UpdateRecordAsync(
                runtime.Tenant,
                runtime.Owner,
                "nodes",
                firstId,
                RuntimeJson.JsonObject(("peer_id", secondId)));

            await using var firstBlocker = new NpgsqlConnection(
                runtime.Factory.CreateConnectionString(runtime.Tenant));
            await using var secondBlocker = new NpgsqlConnection(
                runtime.Factory.CreateConnectionString(runtime.Tenant));
            await Task.WhenAll(firstBlocker.OpenAsync(), secondBlocker.OpenAsync());
            await using var firstBlockerTransaction = await firstBlocker.BeginTransactionAsync();
            await using var secondBlockerTransaction = await secondBlocker.BeginTransactionAsync();

            async Task LockNodeAsync(
                NpgsqlConnection connection,
                NpgsqlTransaction transaction,
                long recordId)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "select id from app.nodes where id = @id for update";
                command.Parameters.AddWithValue("id", recordId);
                Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(recordId);
            }

            await Task.WhenAll(
                LockNodeAsync(firstBlocker, firstBlockerTransaction, firstId),
                LockNodeAsync(secondBlocker, secondBlockerTransaction, secondId));

            var firstStart = runtime.Workflows.StartActionAsync(
                runtime.Tenant,
                Starter(),
                "nodes",
                firstId,
                "close_with_peer");
            var secondStart = runtime.Workflows.StartActionAsync(
                runtime.Tenant,
                Starter(),
                "nodes",
                secondId,
                "close_with_peer");

            await Task.Delay(150);
            firstStart.IsCompleted.Should().BeFalse();
            secondStart.IsCompleted.Should().BeFalse();

            // Each source waiter is already first in its row-lock queue. Releasing
            // both blockers lets each start own one source before requesting the
            // other's correlation parent, creating the inverse lock order that
            // the bounded public retry must absorb.
            await Task.WhenAll(
                firstBlockerTransaction.CommitAsync(),
                secondBlockerTransaction.CommitAsync());

            var starts = await Task.WhenAll(firstStart, secondStart)
                .WaitAsync(TimeSpan.FromSeconds(10));
            starts.Should().OnlyContain(result => result.Instance.Status == "completed");
            (await runtime.Records.GetRecordAsync(runtime.Tenant, "nodes", firstId))!["state"]
                .Should().Be("closed");
            (await runtime.Records.GetRecordAsync(runtime.Tenant, "nodes", secondId))!["state"]
                .Should().Be("closed");
            (await CountInstancesAsync(runtime, "node_close", "node", firstId)).Should().Be(1);
            (await CountInstancesAsync(runtime, "node_close", "node", secondId)).Should().Be(1);
        });
    }

    [PostgresFact]
    public async Task Guarded_action_route_sanitizes_rejections_and_metadata_failures_without_writes()
    {
        await WithRuntimeAsync("workflow_guard_api", async runtime =>
        {
            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                new RuntimeDatabase(runtime.Factory, new RuntimeSqlBuilder()),
                runtime.Tenant,
                "api-owner@workflow-guard.test");
            var blocked = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                owner,
                "subscriptions",
                RuntimeJson.JsonObject(("name", "API blocked")));
            var blockedId = Convert.ToInt64(blocked["id"]);
            await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                owner,
                "onboarding_tasks",
                RuntimeJson.JsonObject(
                    ("label", "API pending"),
                    ("subscription_id", blockedId),
                    ("status", "pending")));

            var sessionSecret = $"workflow-guard-api-{Guid.NewGuid():N}";
            using var application = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Runtime:SessionSecret"] = sessionSecret,
                            ["RuntimeDatabase:TenantConnectionStringTemplate"] =
                                TestPostgres.GetRequiredAdminConnectionString()
                        });
                    });
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IControlPlaneClient>();
                        services.AddSingleton<IControlPlaneClient>(new StaticControlPlaneClient(runtime.Tenant));
                    });
                });
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var cookie = RuntimeHttpTestHelper.CreateSessionCookie(
                sessionSecret,
                runtime.Tenant.TenantStableId,
                runtime.Tenant.Host,
                owner.UserId,
                owner.Email,
                owner.Role);

            async Task<string> InvokeGuardedActionAsync(
                string entity,
                long recordId,
                string action)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/runtime/entities/{entity}/records/{recordId}/actions/{action}");
                request.Headers.Host = runtime.Tenant.Host;
                request.Headers.Add("Cookie", cookie);

                using var response = await client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.Conflict);
                var responseBody = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(responseBody);
                json.RootElement.EnumerateObject().Select(property => property.Name)
                    .Should().BeEquivalentTo("code", "message");
                json.RootElement.GetProperty("code").GetString().Should().Be("workflow.start_guard");
                json.RootElement.GetProperty("message").GetString().Should().Be(FixedGuardMessage);
                responseBody.Should().NotContain("customer-secret");
                return responseBody;
            }

            var body = await InvokeGuardedActionAsync("subscriptions", blockedId, "activate");
            body.Should().NotContain("no_pending_tasks").And.NotContain(RawSensitiveError);
            (await CountInstancesAsync(runtime, "subscription_activation", "subscription", blockedId))
                .Should().Be(0);
            (await CountWorkflowEventsAsync(runtime, "subscription_activation", blockedId))
                .Should().Be(0);

            var missingParentRequest = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                owner,
                "activation_requests",
                RuntimeJson.JsonObject(("name", "API missing parent")));
            var missingParentRequestId = Convert.ToInt64(missingParentRequest["id"]);
            var missingParentBody = await InvokeGuardedActionAsync(
                "activation_requests",
                missingParentRequestId,
                "activate_from_subscription");
            missingParentBody.Should().Be(body,
                "a missing correlation must use the same sanitized API boundary as any other guard rejection");

            var archivedParent = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                owner,
                "subscriptions",
                RuntimeJson.JsonObject(("name", "API archived parent")));
            var archivedParentId = Convert.ToInt64(archivedParent["id"]);
            var archivedParentRequest = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                owner,
                "activation_requests",
                RuntimeJson.JsonObject(
                    ("name", "API archived parent request"),
                    ("subscription_id", archivedParentId)));
            var archivedParentRequestId = Convert.ToInt64(archivedParentRequest["id"]);
            await runtime.Records.ArchiveRecordAsync(
                runtime.Tenant,
                owner,
                "subscriptions",
                archivedParentId);

            var archivedParentBody = await InvokeGuardedActionAsync(
                "activation_requests",
                archivedParentRequestId,
                "activate_from_subscription");
            archivedParentBody.Should().Be(body,
                "an archived correlation parent must not be distinguishable from another guard rejection");
            foreach (var requestId in new[] { missingParentRequestId, archivedParentRequestId })
            {
                (await CountInstancesAsync(
                        runtime,
                        "activation_request_start",
                        "activation_request",
                        requestId))
                    .Should().Be(0);
                (await CountWorkflowEventsAsync(runtime, "activation_request_start", requestId))
                    .Should().Be(0);
            }

            var corrupt = await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                owner,
                "subscriptions",
                RuntimeJson.JsonObject(("name", "API corrupt metadata")));
            var corruptId = Convert.ToInt64(corrupt["id"]);
            await runtime.Records.CreateRecordAsync(
                runtime.Tenant,
                owner,
                "onboarding_tasks",
                RuntimeJson.JsonObject(
                    ("label", "API complete"),
                    ("subscription_id", corruptId),
                    ("status", "done")));
            await CorruptFirstWorkflowStepAsync(runtime, "subscription_activation");

            using var corruptRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/runtime/entities/subscriptions/records/{corruptId}/actions/activate");
            corruptRequest.Headers.Host = runtime.Tenant.Host;
            corruptRequest.Headers.Add("Cookie", cookie);
            using var corruptResponse = await client.SendAsync(corruptRequest);
            corruptResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var corruptBody = await corruptResponse.Content.ReadAsStringAsync();
            using var corruptJson = JsonDocument.Parse(corruptBody);
            corruptJson.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().Equal("message");
            corruptJson.RootElement.GetProperty("message").GetString()
                .Should().Be("Workflow request is invalid.");
            corruptBody.Should().NotContain("customer-secret")
                .And.NotContain("unsupported")
                .And.NotContain("subscription_activation");
            (await CountInstancesAsync(runtime, "subscription_activation", "subscription", corruptId))
                .Should().Be(0);
        });
    }

    [PostgresFact]
    public async Task Acceptance_runner_proves_zero_and_one_instances_and_exact_workflow_permission_denial()
    {
        await WithRuntimeAsync("workflow_guard_accept", async runtime =>
        {
            var runner = new RuntimeBundleTestRunner(
                runtime.Factory,
                new RuntimeSeedRunner(runtime.Records),
                runtime.Records,
                runtime.Workflows,
                new RuntimeActionStore(runtime.Factory, new RuntimePermissionService(runtime.Factory)),
                new RuntimeReportStore(runtime.Factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty workflow guard fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var tests = new[]
            {
                GuardAcceptanceTest("guard_rejected", "pending", expectedCount: 0, seedHash),
                PermissionAcceptanceTest(seedHash),
                RecordPermissionAcceptanceTest(seedHash),
                GuardAcceptanceTest("guard_passed", "done", expectedCount: 1, seedHash)
            };

            BundleAcceptanceTestContractHasher.Stamp(tests);
            var result = await runner.RunAsync(
                runtime.Tenant,
                runtime.Compiled.Version,
                runtime.Compiled.BundleHash,
                seed,
                tests);

            result.Status.Should().Be("passed", string.Join("; ", result.Results.SelectMany(item => item.Failures)));
            result.Results.Should().HaveCount(4).And.OnlyContain(item => item.Status == "passed");
            var rejectedStep = result.Results.Single(item => item.Key == "guard_rejected")
                .StepObservations.Single(item => item.StepId == "start_subscription");
            rejectedStep.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.BusinessRuleRejected);
            rejectedStep.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.WorkflowStartGuard);
            rejectedStep.ActualTarget.Should().Be("no_pending_tasks");

            var permissionStep = result.Results.Single(item => item.Key == "workflow_permission_denied")
                .StepObservations.Single(item => item.StepId == "denied_start");
            permissionStep.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
            permissionStep.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.PermissionDenied);
            permissionStep.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowStart);
            permissionStep.ActualTarget.Should().Be("subscription_activation");

            var recordPermissionStep = result.Results.Single(item => item.Key == "workflow_record_permission_denied")
                .StepObservations.Single(item => item.StepId == "denied_start");
            recordPermissionStep.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
            recordPermissionStep.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.PermissionDenied);
            recordPermissionStep.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.PermissionRecordStartWorkflow);
            recordPermissionStep.ActualTarget.Should().Be("subscription");

            foreach (var (key, count) in new[]
                     {
                         ("guard_rejected", 0),
                         ("workflow_permission_denied", 0),
                         ("workflow_record_permission_denied", 0),
                         ("guard_passed", 1)
                     })
            {
                var observation = result.Results.Single(item => item.Key == key)
                    .AssertionObservations.Single(item => item.AssertionType == "workflow_instance_count");
                observation.Status.Should().Be(RuntimeBundleTestAssertionObservationStatuses.Passed);
                observation.Workflow.Should().Be("subscription_activation");
                observation.WorkflowRef.Should().BeEmpty();
                observation.ExpectedValue.Should().BeNull("V3 evidence keeps only bounded typed digests");
                observation.ActualValue.Should().BeNull("V3 evidence keeps only bounded typed digests");
                observation.RowCount.Should().Be(count);
                observation.ExpectedValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.Integer);
                observation.ActualValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.Integer);
                observation.ExpectedValueDigest.Should().HaveLength(64);
                observation.ActualValueDigest.Should().HaveLength(64);
            }
        });
    }

    [PostgresFact]
    public async Task Acceptance_runner_rejects_an_unknown_workflow_instance_count_target()
    {
        await WithRuntimeAsync("workflow_count_identity", async runtime =>
        {
            var runner = new RuntimeBundleTestRunner(
                runtime.Factory,
                new RuntimeSeedRunner(runtime.Records),
                runtime.Records,
                runtime.Workflows,
                new RuntimeActionStore(runtime.Factory, new RuntimePermissionService(runtime.Factory)),
                new RuntimeReportStore(runtime.Factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty workflow identity fixture" }
            };
            var test = GuardAcceptanceTest(
                "unknown_workflow_count",
                "done",
                expectedCount: 1,
                BundleSeedContractHasher.ComputeHash(seed));
            var original = test.Assertions.Single();
            test.Assertions[0] = new BundleAcceptanceAssertion
            {
                Id = original.Id,
                AfterStep = original.AfterStep,
                Type = original.Type,
                Entity = original.Entity,
                Record = new BundleRecordSelector { Ref = original.Record.Ref },
                Workflow = "unknown_subscription_workflow",
                Count = original.Count
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                runtime.Tenant,
                runtime.Compiled.Version,
                runtime.Compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("failed");
            var observation = result.Results.Single().AssertionObservations.Single();
            observation.Status.Should().Be(RuntimeBundleTestAssertionObservationStatuses.ExecutionError);
            observation.AssertionId.Should().Be("exact_instance_count");
            observation.AssertionType.Should().Be("workflow_instance_count");
            observation.AfterStep.Should().Be("start_subscription");
            observation.Entity.Should().Be("subscription");
            observation.RecordRef.Should().Be("create_subscription");
            observation.Workflow.Should().Be("unknown_subscription_workflow");
            observation.WorkflowRef.Should().BeEmpty();
            observation.ContractItemHash.Should().HaveLength(64);
            observation.Detail.Should().Be(
                $"{RuntimeBundleTestEvidenceFailureCodes.AssertionExecutionError}:exact_instance_count");
            result.Results.Single().Failures.Should().Equal(observation.Detail);
        });
    }

    private static BundleAcceptanceTestDocument GuardAcceptanceTest(
        string key,
        string taskStatus,
        int expectedCount,
        string seedHash)
    {
        var start = new BundleAcceptanceStep
        {
            Id = "start_subscription",
            Type = "start_workflow_action",
            Entity = "subscription",
            Record = new BundleRecordSelector { Ref = "create_subscription" },
            Action = "activate",
            AsRole = "operator",
            AsActor = "operator"
        };
        if (expectedCount == 0)
        {
            start = new BundleAcceptanceStep
            {
                Id = start.Id,
                Type = start.Type,
                Entity = start.Entity,
                Record = start.Record,
                Action = start.Action,
                AsRole = start.AsRole,
                AsActor = start.AsActor,
                Expected = new BundleAcceptanceExpectedOutcome
                {
                    Outcome = BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                    Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowStartGuard,
                    Target = "no_pending_tasks"
                }
            };
        }

        return new BundleAcceptanceTestDocument
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest { Key = key, Label = $"Workflow guard {key}" },
            Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
            Actors = [new BundleAcceptanceActor { Id = "operator", Roles = ["operator"] }],
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = "create_subscription",
                    Type = "create_record",
                    Entity = "subscription",
                    AsRole = "operator",
                    AsActor = "operator",
                    Values = new Dictionary<string, object?> { ["name"] = key }
                },
                new BundleAcceptanceStep
                {
                    Id = "create_task",
                    Type = "create_record",
                    Entity = "onboarding_task",
                    AsRole = "operator",
                    AsActor = "operator",
                    Values = new Dictionary<string, object?>
                    {
                        ["label"] = $"{key} task",
                        ["subscription"] = new Dictionary<string, object?> { ["ref"] = "create_subscription" },
                        ["status"] = taskStatus
                    }
                },
                start
            ],
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Id = "exact_instance_count",
                    AfterStep = "start_subscription",
                    Type = "workflow_instance_count",
                    Entity = "subscription",
                    Record = new BundleRecordSelector { Ref = "create_subscription" },
                    Workflow = "subscription_activation",
                    Count = expectedCount
                }
            ]
        };
    }

    private static BundleAcceptanceTestDocument PermissionAcceptanceTest(string seedHash) => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest
        {
            Key = "workflow_permission_denied",
            Label = "Workflow start permission denial is exact"
        },
        Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
        Actors =
        [
            new BundleAcceptanceActor { Id = "maker", Roles = ["operator"] },
            new BundleAcceptanceActor { Id = "blocked", Roles = ["no_start"] }
        ],
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "create_subscription",
                Type = "create_record",
                Entity = "subscription",
                AsRole = "operator",
                AsActor = "maker",
                Values = new Dictionary<string, object?> { ["name"] = "Permission denied" }
            },
            new BundleAcceptanceStep
            {
                Id = "denied_start",
                Type = "start_workflow_action",
                Entity = "subscription",
                Record = new BundleRecordSelector { Ref = "create_subscription" },
                Action = "activate",
                AsRole = "no_start",
                AsActor = "blocked",
                Expected = new BundleAcceptanceExpectedOutcome
                {
                    Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    Code = BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowStart,
                    Target = "subscription_activation"
                }
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "denial_created_no_instance",
                AfterStep = "denied_start",
                Type = "workflow_instance_count",
                Entity = "subscription",
                Record = new BundleRecordSelector { Ref = "create_subscription" },
                Workflow = "subscription_activation",
                Count = 0
            }
        ]
    };

    private static BundleAcceptanceTestDocument RecordPermissionAcceptanceTest(string seedHash) => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest
        {
            Key = "workflow_record_permission_denied",
            Label = "Workflow record start permission denial is exact"
        },
        Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
        Actors =
        [
            new BundleAcceptanceActor { Id = "maker", Roles = ["operator"] },
            new BundleAcceptanceActor { Id = "scoped", Roles = ["record_start_own"] }
        ],
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "create_subscription",
                Type = "create_record",
                Entity = "subscription",
                AsRole = "operator",
                AsActor = "maker",
                Values = new Dictionary<string, object?> { ["name"] = "Record permission denied" }
            },
            new BundleAcceptanceStep
            {
                Id = "denied_start",
                Type = "start_workflow_action",
                Entity = "subscription",
                Record = new BundleRecordSelector { Ref = "create_subscription" },
                Action = "activate",
                AsRole = "record_start_own",
                AsActor = "scoped",
                Expected = new BundleAcceptanceExpectedOutcome
                {
                    Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    Code = BundleAcceptanceExpectedOutcomeCodes.PermissionRecordStartWorkflow,
                    Target = "subscription"
                }
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "record_denial_created_no_instance",
                AfterStep = "denied_start",
                Type = "workflow_instance_count",
                Entity = "subscription",
                Record = new BundleRecordSelector { Ref = "create_subscription" },
                Workflow = "subscription_activation",
                Count = 0
            }
        ]
    };

    private static async Task WithRuntimeAsync(
        string prefix,
        Func<GuardRuntime, Task> test)
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_{prefix}_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);
        try
        {
            var tenant = RuntimeTestTenant.Create(
                prefix,
                $"{prefix}.erpgenerator.example",
                database,
                TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var compiled = new ErpBundleCompiler().Compile(Bundle());
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder())
                .ApplyCompiledBundleAsync(tenant, compiled);
            await test(new GuardRuntime(
                tenant,
                factory,
                new RuntimeRecordStore(factory),
                new RuntimeWorkflowStore(factory),
                Owner(),
                compiled));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static async Task<Dictionary<string, object?>> CreateSubscriptionAsync(
        GuardRuntime runtime,
        string name) => await runtime.Records.CreateRecordAsync(
        runtime.Tenant,
        runtime.Owner,
        "subscriptions",
        RuntimeJson.JsonObject(("name", name)));

    private static async Task CreateTaskAsync(
        GuardRuntime runtime,
        long subscriptionId,
        string status,
        string label) => await runtime.Records.CreateRecordAsync(
        runtime.Tenant,
        runtime.Owner,
        "onboarding_tasks",
        RuntimeJson.JsonObject(
            ("label", label),
            ("subscription_id", subscriptionId),
            ("status", status)));

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<int> CountInstancesAsync(
        GuardRuntime runtime,
        string workflow,
        string entity,
        long recordId)
    {
        await using var connection = new NpgsqlConnection(runtime.Factory.CreateConnectionString(runtime.Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from _runtime.workflow_instances
            where workflow_id = @workflow and entity_id = @entity and record_id = @recordId
            """;
        command.Parameters.AddWithValue("workflow", workflow);
        command.Parameters.AddWithValue("entity", entity);
        command.Parameters.AddWithValue("recordId", recordId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountWorkflowEventsAsync(
        GuardRuntime runtime,
        string workflow,
        long recordId)
    {
        await using var connection = new NpgsqlConnection(runtime.Factory.CreateConnectionString(runtime.Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from _runtime.workflow_events event
            join _runtime.workflow_instances instance on instance.id = event.instance_id
            where instance.workflow_id = @workflow and instance.record_id = @recordId
            """;
        command.Parameters.AddWithValue("workflow", workflow);
        command.Parameters.AddWithValue("recordId", recordId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountPendingTasksAsync(GuardRuntime runtime, long subscriptionId)
    {
        await using var connection = new NpgsqlConnection(runtime.Factory.CreateConnectionString(runtime.Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from app.onboarding_tasks
            where subscription_id = @subscriptionId and status = 'pending' and archived_at is null
            """;
        command.Parameters.AddWithValue("subscriptionId", subscriptionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task CorruptFirstWorkflowStepAsync(GuardRuntime runtime, string workflow)
    {
        await using var connection = new NpgsqlConnection(runtime.Factory.CreateConnectionString(runtime.Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.workflow_definitions
            set metadata = jsonb_set(
                metadata,
                '{Steps,0,Set}',
                '{"missing_field":"activated"}'::jsonb)
            where id = @workflow
            """;
        command.Parameters.AddWithValue("workflow", workflow);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static RuntimeUserContext Owner() => new(
        1,
        "owner@workflow-guard.test",
        RuntimeSystemRoleIds.TenantOwner)
    {
        Roles = [RuntimeSystemRoleIds.TenantOwner]
    };

    private static RuntimeUserContext Starter() => new(
        2,
        "starter@workflow-guard.test",
        "starter")
    {
        Roles = ["starter"]
    };

    private static ErpBundle Bundle() => new()
    {
        Id = "workflow_start_guard_runtime",
        Name = "Workflow start guard runtime",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "subscriptions",
                Name = "Subscriptions",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "subscription",
                        Name = "subscriptions",
                        DisplayName = "Subscriptions",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name", Name = "name", DisplayName = "Name",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "status", Name = "status", DisplayName = "Status",
                                Type = "enum", Required = true, Options = ["draft", "activated"],
                                Lifecycle = new ErpFieldLifecycle
                                {
                                    Initial = "draft",
                                    Transitions = "direct"
                                }
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "onboarding_task",
                        Name = "onboarding_tasks",
                        DisplayName = "Onboarding tasks",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "label", Name = "label", DisplayName = "Label",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "subscription", Name = "subscription_id", DisplayName = "Subscription",
                                Type = "relation", Required = true, References = "subscription"
                            },
                            new ErpField
                            {
                                Id = "status", Name = "status", DisplayName = "Status",
                                Type = "enum", Required = true, Options = ["pending", "done"]
                            }
                        ],
                        RelationCreateGuards =
                        [
                            new ErpRelationCreateGuard
                            {
                                RelationField = "subscription",
                                ReferencedField = "status",
                                Op = "eq",
                                Value = new ErpTypedLiteral { Type = "string", Value = "draft" }
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "activation_request",
                        Name = "activation_requests",
                        DisplayName = "Activation requests",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name", Name = "name", DisplayName = "Name",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "subscription", Name = "subscription_id", DisplayName = "Subscription",
                                Type = "relation", Required = false, References = "subscription"
                            },
                            new ErpField
                            {
                                Id = "status", Name = "status", DisplayName = "Status",
                                Type = "enum", Required = true, Options = ["draft", "activated"],
                                Lifecycle = new ErpFieldLifecycle
                                {
                                    Initial = "draft",
                                    Transitions = "direct"
                                }
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "node",
                        Name = "nodes",
                        DisplayName = "Nodes",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name", Name = "name", DisplayName = "Name",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "peer", Name = "peer_id", DisplayName = "Peer",
                                Type = "relation", Required = false, References = "node"
                            },
                            new ErpField
                            {
                                Id = "state", Name = "state", DisplayName = "State",
                                Type = "enum", Required = true, Options = ["open", "closed"],
                                Lifecycle = new ErpFieldLifecycle
                                {
                                    Initial = "open",
                                    Transitions = "direct"
                                }
                            }
                        ]
                    }
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = "subscription_activation",
                        Name = "Subscription activation",
                        Entity = "subscription",
                        Trigger = new ErpWorkflowTrigger { Action = "activate" },
                        StartGuards =
                        [
                            new ErpWorkflowStartGuard
                            {
                                Id = "has_tasks",
                                Entity = "onboarding_task",
                                Where = [CorrelationFilter()],
                                ResultCountOp = "gt",
                                ResultCount = 0,
                                Error = "customer-secret: task existence details"
                            },
                            new ErpWorkflowStartGuard
                            {
                                Id = "no_pending_tasks",
                                Entity = "onboarding_task",
                                Where =
                                [
                                    CorrelationFilter(),
                                    new ErpActionFilter { Field = "status", Op = "eq", Value = "pending" }
                                ],
                                ResultCountOp = "eq",
                                ResultCount = 0,
                                Error = RawSensitiveError
                            }
                        ],
                        Steps = [ActivationStep()]
                    },
                    new ErpWorkflow
                    {
                        Id = "activation_request_start",
                        Name = "Activation request start",
                        Entity = "activation_request",
                        Trigger = new ErpWorkflowTrigger { Action = "activate_from_subscription" },
                        StartGuards =
                        [
                            new ErpWorkflowStartGuard
                            {
                                Id = "z_parent_exists",
                                Entity = "subscription",
                                Where =
                                [
                                    new ErpActionFilter
                                    {
                                        Field = "id",
                                        Op = "eq",
                                        ValueFrom = new ErpActionValueFrom { Record = "subscription" }
                                    }
                                ],
                                ResultCountOp = "eq",
                                ResultCount = 1,
                                Error = "customer-secret: missing optional relation"
                            },
                            new ErpWorkflowStartGuard
                            {
                                Id = "a_parent_exists",
                                Entity = "subscription",
                                Where =
                                [
                                    new ErpActionFilter
                                    {
                                        Field = "id",
                                        Op = "eq",
                                        ValueFrom = new ErpActionValueFrom { Record = "subscription" }
                                    }
                                ],
                                ResultCountOp = "eq",
                                ResultCount = 1,
                                Error = "customer-secret: second missing optional relation"
                            }
                        ],
                        Steps = [ActivationStep()]
                    },
                    new ErpWorkflow
                    {
                        Id = "node_close",
                        Name = "Node close",
                        Entity = "node",
                        Trigger = new ErpWorkflowTrigger { Action = "close_with_peer" },
                        StartGuards =
                        [
                            new ErpWorkflowStartGuard
                            {
                                Id = "peer_exists",
                                Entity = "node",
                                Where =
                                [
                                    new ErpActionFilter
                                    {
                                        Field = "id",
                                        Op = "eq",
                                        ValueFrom = new ErpActionValueFrom { Record = "peer" }
                                    }
                                ],
                                ResultCountOp = "eq",
                                ResultCount = 1,
                                Error = "customer-secret: peer details"
                            }
                        ],
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "mark_node_closed",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["state"] = "closed" }
                            }
                        ]
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["owner"] = new()
            {
                DisplayName = "Owner",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            },
            ["starter"] = new()
            {
                DisplayName = "Starter",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "subscription" },
                    new ErpGrant { Action = "start_workflow", Entity = "subscription" },
                    new ErpGrant { Action = "read", Entity = "activation_request" },
                    new ErpGrant { Action = "start_workflow", Entity = "activation_request" },
                    new ErpGrant { Action = "read", Entity = "node" },
                    new ErpGrant { Action = "start_workflow", Entity = "node" }
                ]
            },
            ["operator"] = new()
            {
                DisplayName = "Operator",
                Grants =
                [
                    new ErpGrant { Action = "create", Entity = "subscription" },
                    new ErpGrant { Action = "read", Entity = "subscription" },
                    new ErpGrant { Action = "start_workflow", Entity = "subscription" },
                    new ErpGrant { Action = "create", Entity = "onboarding_task" },
                    new ErpGrant { Action = "read", Entity = "onboarding_task" },
                    new ErpGrant { Action = "update", Entity = "onboarding_task" }
                ]
            },
            ["no_start"] = new()
            {
                DisplayName = "No start",
                Grants = [new ErpGrant { Action = "read", Entity = "subscription" }]
            },
            ["record_start_own"] = new()
            {
                DisplayName = "Record start own",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "subscription" },
                    new ErpGrant
                    {
                        Action = "start_workflow",
                        Entity = "subscription",
                        Scope = PermissionGrantScopes.Record,
                        RecordScope = new ErpRecordScope
                        {
                            Field = "created_by",
                            Source = RecordPermissionScopeSources.ActorPrincipal
                        }
                    }
                ]
            }
        }
    };

    private static ErpActionFilter CorrelationFilter() => new()
    {
        Field = "subscription",
        Op = "eq",
        ValueFrom = new ErpActionValueFrom { Record = "id" }
    };

    private static ErpWorkflowStep ActivationStep() => new()
    {
        Id = "mark_activated",
        Type = "update_record",
        Set = new Dictionary<string, string> { ["status"] = "activated" }
    };

    private sealed record GuardRuntime(
        ResolvedTenantEnvironment Tenant,
        TenantDatabaseConnectionFactory Factory,
        RuntimeRecordStore Records,
        RuntimeWorkflowStore Workflows,
        RuntimeUserContext Owner,
        CompiledBundle Compiled);
}
