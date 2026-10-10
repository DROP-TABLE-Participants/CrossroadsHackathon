using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Scheduling;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeScheduleStoreIntegrationTests
{
    [PostgresFact]
    public async Task Refresh_materializes_updates_cancels_and_rearms_one_stable_job()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        var firstDue = fixture.Clock.GetUtcNow().AddHours(1);
        var recordId = await fixture.InsertIncidentAsync("open", firstDue);

        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        (await fixture.Store.RefreshAsync(connection, default)).MaterializedOrUpdated.Should().Be(1);
        var initial = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Should().ContainSingle().Which;
        initial.RecordId.Should().Be(recordId);
        initial.Status.Should().Be(RuntimeScheduleJobStatuses.Ready);
        initial.DueAt.Should().Be(firstDue);

        await fixture.UpdateIncidentAsync(recordId, "resolved", firstDue);
        (await fixture.Store.RefreshAsync(connection, default)).Cancelled.Should().Be(1);
        (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single().Status
            .Should().Be(RuntimeScheduleJobStatuses.Cancelled);

        var reopenedDue = firstDue.AddHours(2);
        await fixture.UpdateIncidentAsync(recordId, "open", reopenedDue);
        await fixture.Store.RefreshAsync(connection, default);
        var rearmed = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Should().ContainSingle().Which;
        rearmed.Id.Should().Be(initial.Id, "one-shot identity excludes definition and due-value revisions");
        rearmed.Status.Should().Be(RuntimeScheduleJobStatuses.Ready);
        rearmed.Attempt.Should().Be(0);
        rearmed.DueAt.Should().Be(reopenedDue);

        (await fixture.Store.ClaimNextAsync(connection, "worker_a", default)).Should().BeNull();
        fixture.Clock.Advance(TimeSpan.FromHours(4));
        (await fixture.Store.ClaimNextAsync(connection, "worker_a", default))
            .Should().NotBeNull();
    }

    [PostgresFact]
    public async Task Claims_heartbeat_reclaim_backoff_exhaustion_and_conditional_writes_are_closed()
    {
        var options = new RuntimeScheduleOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(1),
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            BaseRetryDelay = TimeSpan.FromMinutes(1),
            MaxRetryDelay = TimeSpan.FromMinutes(5),
            MaxAttempts = 2,
            MaxJobsPerCycle = 10
        };
        await using var fixture = await ScheduleFixture.CreateAsync(options);
        await fixture.InsertIncidentAsync("open", fixture.Clock.GetUtcNow().AddMinutes(-2));
        await fixture.InsertIncidentAsync("open", fixture.Clock.GetUtcNow().AddMinutes(-1));

        await using var firstConnection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await using var secondConnection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await fixture.Store.RefreshAsync(firstConnection, default);
        var first = await fixture.Store.ClaimNextAsync(firstConnection, "worker_a", default);
        var second = await fixture.Store.ClaimNextAsync(secondConnection, "worker_b", default);
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Id.Should().NotBe(second!.Id, "SKIP LOCKED claims must never duplicate one job");

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        (await fixture.Store.HeartbeatAsync(firstConnection, first.Lease, default)).Should().BeTrue();
        var staleLease = first.Lease with { LeaseId = Guid.NewGuid() };
        (await fixture.Store.HeartbeatAsync(firstConnection, staleLease, default)).Should().BeFalse();

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var reclaim = await fixture.Store.RefreshAsync(firstConnection, default);
        reclaim.Reclaimed.Should().Be(2);
        reclaim.Exhausted.Should().Be(0);
        (await fixture.Store.ListJobsAsync(fixture.Tenant)).Should().OnlyContain(job =>
            job.Status == RuntimeScheduleJobStatuses.Retry &&
            job.LastErrorCode == RuntimeScheduleErrorCodes.LeaseExpired);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var finalAttempt = await fixture.Store.ClaimNextAsync(firstConnection, "worker_a", default);
        finalAttempt.Should().NotBeNull();
        finalAttempt!.Attempt.Should().Be(2);
        (await fixture.Store.FailAsync(
                firstConnection,
                finalAttempt,
                RuntimeScheduleErrorCodes.WorkflowFailed,
                default))
            .Should().Be(RuntimeScheduleJobStatuses.Exhausted);
        (await fixture.Store.HeartbeatAsync(firstConnection, finalAttempt.Lease, default)).Should().BeFalse();
    }

    [PostgresFact]
    public async Task Definition_change_rebinds_only_superseded_no_workflow_row_without_refiring_identity()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        var recordId = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddHours(1));
        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await fixture.Store.RefreshAsync(connection, default);
        var original = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();

        await fixture.UpdateIncidentAsync(recordId, "resolved", original.DueAt);
        await fixture.Store.RefreshAsync(connection, default);
        (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single().Status
            .Should().Be(RuntimeScheduleJobStatuses.Cancelled);

        await fixture.DeployAsync("2.0.0", "resolved");
        var superseded = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();
        superseded.Id.Should().Be(original.Id);
        superseded.Status.Should().Be(RuntimeScheduleJobStatuses.Superseded);
        superseded.WorkflowInstanceId.Should().BeNull();

        await fixture.Store.RefreshAsync(connection, default);
        var rebound = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();
        rebound.Id.Should().Be(original.Id);
        rebound.ScheduleDefinitionHash.Should().NotBe(original.ScheduleDefinitionHash);
        rebound.Status.Should().Be(RuntimeScheduleJobStatuses.Ready);
        rebound.Attempt.Should().Be(0);
    }

    [PostgresFact]
    public async Task Materialization_executes_every_closed_filter_operator_with_exact_null_semantics()
    {
        await using var fixture = await ScheduleFixture.CreateAsync(allOperatorSchedules: true);
        await fixture.InsertIncidentAsync(
            "open",
            new DateTimeOffset(2026, 7, 13, 13, 0, 0, TimeSpan.Zero));
        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);

        await fixture.Store.RefreshAsync(connection, default);

        (await fixture.Store.ListJobsAsync(fixture.Tenant))
            .Select(job => job.ScheduleId)
            .Should().BeEquivalentTo(
                "filter_eq", "filter_neq", "filter_in", "filter_not_in",
                "filter_gt", "filter_gte", "filter_lt", "filter_lte",
                "filter_exists", "filter_not_exists");
    }

    [PostgresFact]
    public async Task Completion_is_idempotent_only_for_exact_scheduler_bound_workflow_instance()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        await fixture.InsertIncidentAsync("open", fixture.Clock.GetUtcNow().AddMinutes(-1));
        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await fixture.Store.RefreshAsync(connection, default);
        var job = (await fixture.Store.ClaimNextAsync(connection, "worker_a", default))!;

        long workflowInstanceId;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into _runtime.workflow_instances (
                  workflow_id, workflow_name, entity_id, entity_key, record_id,
                  status, trigger_kind, trigger_key, scheduler_job_id)
                values (
                  'escalate_incident', 'Escalate incident', 'incident', 'incident', @recordId,
                  'completed', 'schedule', 'incident_due', @jobId)
                returning id
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("recordId", job.RecordId);
            insert.Parameters.AddWithValue("jobId", job.Id);
            workflowInstanceId = Convert.ToInt64(await insert.ExecuteScalarAsync());

            await using var complete = new NpgsqlCommand(
                """
                update _runtime.scheduled_jobs
                set status = 'completed', workflow_instance_id = @workflowInstanceId,
                    lease_id = null, leased_by = null, lease_expires_at = null,
                    completed_at = @now, updated_at = @now
                where id = @jobId and status = 'running'
                """,
                connection,
                transaction);
            complete.Parameters.AddWithValue("workflowInstanceId", workflowInstanceId);
            complete.Parameters.AddWithValue("jobId", job.Id);
            complete.Parameters.AddWithValue("now", fixture.Clock.GetUtcNow());
            (await complete.ExecuteNonQueryAsync()).Should().Be(1);
            await transaction.CommitAsync();
        }

        (await fixture.Store.CompleteAsync(connection, job.Lease, workflowInstanceId, default))
            .Should().BeTrue("the authoritative executor already committed the exact atomic boundary");
        (await fixture.Store.CompleteAsync(connection, job.Lease, workflowInstanceId + 1, default))
            .Should().BeFalse();
    }

    [PostgresFact]
    public async Task Cycle_yields_without_mutation_while_acceptance_database_fence_is_held()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        await fixture.InsertIncidentAsync("open", fixture.Clock.GetUtcNow().AddMinutes(-1));
        await using var blocker = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        (await RuntimeScheduleStore.TryAcquireAcceptanceFenceAsync(blocker, default)).Should().BeTrue();
        try
        {
            var cycle = new RuntimeScheduleCycle(
                fixture.Store,
                new RejectUnexpectedExecution(),
                fixture.Options);

            var result = await cycle.RunOnceAsync(fixture.Tenant, "worker_a");

            result.AcceptanceFenceAcquired.Should().BeFalse();
            (await fixture.Store.ListJobsAsync(fixture.Tenant)).Should().BeEmpty();
        }
        finally
        {
            await RuntimeScheduleStore.ReleaseAcceptanceFenceAsync(blocker);
        }
    }

    [PostgresFact]
    public async Task Cycle_quietly_noops_for_a_legacy_tenant_without_scheduler_metadata()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var database = $"erp_tenant_schedule_legacy_{Guid.NewGuid():N}"[..56];
        await TestPostgres.CreateDatabaseAsync(admin, database);
        try
        {
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var tenant = RuntimeTestTenant.Create(
                "schedule-legacy",
                "schedule-legacy.erpgenerator.example",
                database);
            var executor = new RejectUnexpectedExecution();
            var cycle = new RuntimeScheduleCycle(new RuntimeScheduleStore(factory), executor);

            var first = await cycle.RunOnceAsync(tenant, "worker_a");
            var second = await cycle.RunOnceAsync(tenant, "worker_a");

            first.Should().Be(new RuntimeScheduleCycleResult(false, 0, 0, 0, 0, 0, 0, 0, 0));
            second.Should().Be(new RuntimeScheduleCycleResult(false, 0, 0, 0, 0, 0, 0, 0, 0));
            executor.Calls.Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, database);
        }
    }

    [PostgresFact]
    public async Task Cycle_uses_a_non_pooled_provisioned_tenant_credential_without_admin_and_completes_workflow()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_schedule_cred_{suffix}";
        var secretReference = $"tenant-db:schedule-cred-{suffix}:staging:{database}";
        var tenant = RuntimeTestTenant.Create(
            $"schedule-cred-{suffix}",
            $"schedule-cred-{suffix}.erpgenerator.example",
            database) with
        {
            DatabaseCredentialMode = "secret_ref",
            DatabaseCredentialSecretRef = secretReference
        };
        var credentials = new InMemoryTenantDatabaseCredentialStore();
        var setupOptions = Options.Create(new RuntimeDatabaseOptions
        {
            AdminConnectionString = admin,
            AutoProvisionDatabases = true,
            CredentialMode = TenantDatabaseCredentialMode.Provisioned,
            ProvisionTenantAppUsers = true,
            TenantAppUserPrefix = "erp_schedule_test"
        });
        var provisioner = new PostgresTenantDatabaseProvisioner(setupOptions, credentials);
        var setupFactory = new TenantDatabaseConnectionFactory(setupOptions, credentials);
        string? appUserName = null;
        try
        {
            await provisioner.ProvisionAsync(tenant);
            credentials.TryGet(tenant, out var provisionedCredential).Should().BeTrue();
            appUserName = provisionedCredential.UserName;
            credentials.Store(provisionedCredential with { SecretReference = secretReference });
            var compiled = new ErpBundleCompiler().Compile(
                ScheduleFixture.Bundle("1.0.0", "open", allOperatorSchedules: false));
            await new RuntimeDatabase(setupFactory, new RuntimeSqlBuilder(), provisioner)
                .ApplyCompiledBundleAsync(tenant, compiled);

            var productionOptions = Options.Create(new RuntimeDatabaseOptions
            {
                CredentialMode = TenantDatabaseCredentialMode.Provisioned,
                AutoProvisionDatabases = false,
                AdminConnectionString = string.Empty,
                TenantConnectionStringTemplate = string.Empty
            });
            var runtimeFactory = new TenantDatabaseConnectionFactory(productionOptions, credentials);
            var schedulerConnection = new NpgsqlConnectionStringBuilder(
                runtimeFactory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Scheduler));
            schedulerConnection.Username.Should().Be(appUserName);
            schedulerConnection.Pooling.Should().BeFalse();
            var provisioningConnection = () => runtimeFactory.CreateConnectionString(
                tenant,
                TenantDatabaseConnectionPurpose.Provisioning);
            provisioningConnection.Should().Throw<InvalidOperationException>()
                .WithMessage("*connection string template is not configured*");

            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
            long recordId;
            await using (var connection = new NpgsqlConnection(runtimeFactory.CreateConnectionString(tenant)))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    insert into app.incidents (title, response_deadline, severity, status)
                    values (@title, @dueAt, 'p1', 'open')
                    returning id
                    """;
                insert.Parameters.AddWithValue("title", $"Credential scheduler incident {suffix}");
                insert.Parameters.AddWithValue("dueAt", clock.GetUtcNow().AddMinutes(-1));
                recordId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            }

            var store = new RuntimeScheduleStore(runtimeFactory, timeProvider: clock);
            var cycle = new RuntimeScheduleCycle(
                store,
                new RuntimeScheduledWorkflowExecutor(
                    new RuntimeWorkflowStore(
                        runtimeFactory,
                        new RuntimePermissionService(runtimeFactory),
                        clock)));

            var result = await cycle.RunOnceAsync(tenant, "production-shaped-worker");

            result.AcceptanceFenceAcquired.Should().BeTrue();
            result.Claimed.Should().Be(1);
            result.Completed.Should().Be(1);
            result.Retried.Should().Be(0);
            result.LeaseLost.Should().Be(0);
            var job = (await store.ListJobsAsync(tenant)).Should().ContainSingle().Which;
            job.Status.Should().Be(RuntimeScheduleJobStatuses.Completed);
            job.WorkflowInstanceId.Should().NotBeNull();
            await using var verification = new NpgsqlConnection(runtimeFactory.CreateConnectionString(tenant));
            await verification.OpenAsync();
            await using (var status = verification.CreateCommand())
            {
                status.CommandText = "select status from app.incidents where id = @recordId";
                status.Parameters.AddWithValue("recordId", recordId);
                (await status.ExecuteScalarAsync()).Should().Be("escalated");
            }

            await using (var lineage = verification.CreateCommand())
            {
                lineage.CommandText = """
                    select trigger_kind, trigger_key, scheduler_job_id, started_by
                    from _runtime.workflow_instances
                    where id = @workflowInstanceId
                    """;
                lineage.Parameters.AddWithValue("workflowInstanceId", job.WorkflowInstanceId!.Value);
                await using var reader = await lineage.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("schedule");
                reader.GetString(1).Should().Be("incident_due");
                reader.GetInt64(2).Should().Be(job.Id);
                reader.IsDBNull(3).Should().BeTrue("scheduled execution must not synthesize a business user");
            }
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, database);
            if (!string.IsNullOrWhiteSpace(appUserName))
            {
                await DropRoleAsync(admin, appUserName);
            }
        }
    }

    [PostgresFact]
    public async Task Deployment_waits_for_the_same_outer_scheduler_and_acceptance_fence()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        var due = fixture.Clock.GetUtcNow().AddHours(1);
        var recordId = await fixture.InsertIncidentAsync("open", due);
        await using (var initialConnection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default))
        {
            await fixture.Store.RefreshAsync(initialConnection, default);
        }
        var initial = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();
        await using var blocker = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        (await RuntimeScheduleStore.TryAcquireAcceptanceFenceAsync(blocker, default)).Should().BeTrue();
        var deployment = fixture.DeployAsync("2.0.0", "resolved");
        try
        {
            var winner = await Task.WhenAny(deployment, Task.Delay(TimeSpan.FromMilliseconds(250)));
            winner.Should().NotBe(deployment,
                "deployment must acquire the exact scheduler/acceptance fence before any DDL or metadata mutation");
        }
        finally
        {
            await RuntimeScheduleStore.ReleaseAcceptanceFenceAsync(blocker);
        }

        await deployment;
        await fixture.UpdateIncidentAsync(recordId, "resolved", due);
        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await fixture.Store.RefreshAsync(connection, default);
        var job = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();
        job.Id.Should().Be(initial.Id);
        job.ScheduleDefinitionHash.Should().NotBe(initial.ScheduleDefinitionHash);
        job.Status.Should().Be(RuntimeScheduleJobStatuses.Ready,
            "the deployment supersedes the old job and the next refresh safely rebinds the stable identity");
    }

    [PostgresFact]
    public async Task Due_cycle_starts_one_system_workflow_atomically_and_restart_does_not_duplicate_it()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        var recordId = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddMinutes(-1));
        var workflows = new RuntimeWorkflowStore(
            fixture.ConnectionFactory,
            new RuntimePermissionService(fixture.ConnectionFactory),
            fixture.Clock);
        var cycle = new RuntimeScheduleCycle(
            fixture.Store,
            new RuntimeScheduledWorkflowExecutor(workflows),
            fixture.Options);

        var first = await cycle.RunOnceAsync(fixture.Tenant, "scheduler_a");

        first.AcceptanceFenceAcquired.Should().BeTrue();
        first.Claimed.Should().Be(1);
        first.Completed.Should().Be(1);
        first.Retried.Should().Be(0);
        first.LeaseLost.Should().Be(0);
        var job = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Should().ContainSingle().Which;
        job.Status.Should().Be(RuntimeScheduleJobStatuses.Completed);
        job.WorkflowInstanceId.Should().NotBeNull();
        (await fixture.ScalarAsync<string>(
                "select status from app.incidents where id = @recordId",
                ("recordId", recordId)))
            .Should().Be("escalated");
        (await fixture.ScalarAsync<long?>(
                "select updated_by from app.incidents where id = @recordId",
                ("recordId", recordId)))
            .Should().BeNull("scheduled execution must not fabricate a runtime user");
        (await fixture.ScalarAsync<long>(
                """
                select count(*)
                from _runtime.workflow_instances
                where scheduler_job_id = @jobId
                  and trigger_kind = 'schedule'
                  and trigger_key = 'incident_due'
                  and started_by is null
                """,
                ("jobId", job.Id)))
            .Should().Be(1);
        (await fixture.ScalarAsync<long>(
                """
                select count(*)
                from audit.events
                where event_type = 'workflow.started'
                  and metadata ->> 'schedulerJobId' = @jobId
                  and metadata -> 'runtimeUserId' = 'null'::jsonb
                """,
                ("jobId", job.Id.ToString())))
            .Should().Be(1);

        var restartedStore = new RuntimeScheduleStore(
            fixture.ConnectionFactory,
            fixture.Options,
            fixture.Clock);
        var restartedWorkflows = new RuntimeWorkflowStore(
            fixture.ConnectionFactory,
            new RuntimePermissionService(fixture.ConnectionFactory),
            fixture.Clock);
        var afterRestart = await new RuntimeScheduleCycle(
                restartedStore,
                new RuntimeScheduledWorkflowExecutor(restartedWorkflows),
                fixture.Options)
            .RunOnceAsync(fixture.Tenant, "scheduler_b");

        afterRestart.Claimed.Should().Be(0);
        (await fixture.ScalarAsync<long>(
                "select count(*) from _runtime.workflow_instances where scheduler_job_id = @jobId",
                ("jobId", job.Id)))
            .Should().Be(1);
        (await fixture.ScalarAsync<long>(
                "select count(*) from audit.events where event_type = 'workflow.started' and metadata ->> 'schedulerJobId' = @jobId",
                ("jobId", job.Id.ToString())))
            .Should().Be(1);
    }

    [PostgresFact]
    public async Task Executor_rechecks_locked_record_and_rearms_or_cancels_without_workflow_effects()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        var dueRecord = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddMinutes(-2));
        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await fixture.Store.RefreshAsync(connection, default);
        var dueJob = await fixture.Store.ClaimNextAsync(connection, "scheduler_a", default);
        dueJob.Should().NotBeNull();
        var movedDue = fixture.Clock.GetUtcNow().AddHours(2);
        await fixture.UpdateIncidentAsync(dueRecord, "open", movedDue);
        var executor = new RuntimeScheduledWorkflowExecutor(new RuntimeWorkflowStore(
            fixture.ConnectionFactory,
            new RuntimePermissionService(fixture.ConnectionFactory),
            fixture.Clock));

        var moved = () => executor.ExecuteAsync(fixture.Tenant, dueJob!, default);
        await moved.Should().ThrowAsync<RuntimeScheduleLeaseLostException>();

        var rearmed = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single(job => job.Id == dueJob!.Id);
        rearmed.Status.Should().Be(RuntimeScheduleJobStatuses.Ready);
        rearmed.DueAt.Should().Be(movedDue);
        (await fixture.ScalarAsync<long>(
                "select count(*) from _runtime.workflow_instances where scheduler_job_id = @jobId",
                ("jobId", dueJob!.Id)))
            .Should().Be(0);

        var ineligibleRecord = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddMinutes(-1));
        await fixture.Store.RefreshAsync(connection, default);
        var ineligibleJob = await fixture.Store.ClaimNextAsync(connection, "scheduler_a", default);
        ineligibleJob.Should().NotBeNull();
        await fixture.UpdateIncidentAsync(ineligibleRecord, "resolved", ineligibleJob!.DueAt);

        var ineligible = () => executor.ExecuteAsync(fixture.Tenant, ineligibleJob, default);
        await ineligible.Should().ThrowAsync<RuntimeScheduleLeaseLostException>();

        (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single(job => job.Id == ineligibleJob.Id)
            .Status.Should().Be(RuntimeScheduleJobStatuses.Cancelled);
        (await fixture.ScalarAsync<long>(
                "select count(*) from _runtime.workflow_instances where scheduler_job_id = @jobId",
                ("jobId", ineligibleJob.Id)))
            .Should().Be(0);
    }

    [PostgresFact]
    public async Task Executor_binds_the_claimed_record_to_the_locked_job_row()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        await fixture.InsertIncidentAsync("open", fixture.Clock.GetUtcNow().AddMinutes(-2));
        var otherRecordId = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddMinutes(-1));
        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await fixture.Store.RefreshAsync(connection, default);
        var job = await fixture.Store.ClaimNextAsync(connection, "scheduler_a", default);
        var forged = job! with { RecordId = otherRecordId };
        var executor = new RuntimeScheduledWorkflowExecutor(new RuntimeWorkflowStore(
            fixture.ConnectionFactory,
            new RuntimePermissionService(fixture.ConnectionFactory),
            fixture.Clock));

        var execute = () => executor.ExecuteAsync(fixture.Tenant, forged, default);

        await execute.Should().ThrowAsync<RuntimeScheduleLeaseLostException>();
        (await fixture.ScalarAsync<long>(
                "select count(*) from _runtime.workflow_instances where scheduler_job_id = @jobId",
                ("jobId", job.Id)))
            .Should().Be(0);
        (await fixture.ScalarAsync<string>(
                "select status from app.incidents where id = @recordId",
                ("recordId", otherRecordId)))
            .Should().Be("open");
    }

    [PostgresFact]
    public async Task Moved_due_rearm_does_not_consume_the_only_execution_attempt()
    {
        var options = new RuntimeScheduleOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(1),
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            BaseRetryDelay = TimeSpan.FromMinutes(1),
            MaxRetryDelay = TimeSpan.FromMinutes(1),
            MaxAttempts = 1,
            MaxJobsPerCycle = 10
        };
        await using var fixture = await ScheduleFixture.CreateAsync(options);
        var recordId = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddMinutes(-1));
        await using var connection = await fixture.Store.OpenConnectionAsync(fixture.Tenant, default);
        await fixture.Store.RefreshAsync(connection, default);
        var job = await fixture.Store.ClaimNextAsync(connection, "scheduler_a", default);
        var movedDue = fixture.Clock.GetUtcNow().AddHours(1);
        await fixture.UpdateIncidentAsync(recordId, "open", movedDue);
        var executor = new RuntimeScheduledWorkflowExecutor(new RuntimeWorkflowStore(
            fixture.ConnectionFactory,
            new RuntimePermissionService(fixture.ConnectionFactory),
            fixture.Clock));

        Func<Task> execute = () => executor.ExecuteAsync(fixture.Tenant, job!, default);
        await execute.Should().ThrowAsync<RuntimeScheduleLeaseLostException>();
        var rearmed = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();
        rearmed.Status.Should().Be(RuntimeScheduleJobStatuses.Ready);
        rearmed.Attempt.Should().Be(0);

        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var reclaimed = await fixture.Store.ClaimNextAsync(connection, "scheduler_b", default);
        reclaimed.Should().NotBeNull();
        reclaimed!.Id.Should().Be(job!.Id);
        reclaimed.Attempt.Should().Be(1);
    }

    [PostgresFact]
    public async Task Workflow_failure_rolls_back_all_effects_before_the_job_enters_retry()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        var recordId = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddMinutes(-1));
        await fixture.ExecuteAsync(
            """
            update _runtime.workflow_definitions
            set metadata = jsonb_set(
                metadata,
                '{Steps}',
                (metadata -> 'Steps') ||
                '[{"Id":"invalid_after_effect","Type":"update_record","Set":{"missing_field":"x"}}]'::jsonb)
            where id = 'escalate_incident'
            """);
        var workflows = new RuntimeWorkflowStore(
            fixture.ConnectionFactory,
            new RuntimePermissionService(fixture.ConnectionFactory),
            fixture.Clock);
        var cycle = new RuntimeScheduleCycle(
            fixture.Store,
            new RuntimeScheduledWorkflowExecutor(workflows),
            fixture.Options);

        var result = await cycle.RunOnceAsync(fixture.Tenant, "scheduler_a");

        result.Completed.Should().Be(0);
        result.Retried.Should().Be(1);
        (await fixture.ScalarAsync<string>(
                "select status from app.incidents where id = @recordId",
                ("recordId", recordId)))
            .Should().Be("open", "the earlier workflow update must roll back with the later failure");
        (await fixture.ScalarAsync<long>(
                "select count(*) from _runtime.workflow_instances where record_id = @recordId",
                ("recordId", recordId)))
            .Should().Be(0);
        var job = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();
        job.Status.Should().Be(RuntimeScheduleJobStatuses.Retry);
        job.LastErrorCode.Should().Be(RuntimeScheduleErrorCodes.WorkflowFailed);
        job.WorkflowInstanceId.Should().BeNull();
    }

    [PostgresFact]
    public async Task Cycle_atomically_executes_triggerless_workflow_once_without_a_business_user()
    {
        await using var fixture = await ScheduleFixture.CreateAsync();
        var recordId = await fixture.InsertIncidentAsync(
            "open",
            fixture.Clock.GetUtcNow().AddMinutes(-1));
        var cycle = new RuntimeScheduleCycle(
            fixture.Store,
            fixture.CreateWorkflowExecutor(),
            fixture.Options);

        var first = await cycle.RunOnceAsync(fixture.Tenant, "worker_a");

        first.AcceptanceFenceAcquired.Should().BeTrue();
        first.Claimed.Should().Be(1);
        first.Completed.Should().Be(1);
        first.LeaseLost.Should().Be(0);
        var job = (await fixture.Store.ListJobsAsync(fixture.Tenant)).Single();
        job.Status.Should().Be(RuntimeScheduleJobStatuses.Completed);
        job.WorkflowInstanceId.Should().NotBeNull();
        (await fixture.ReadIncidentStatusAsync(recordId)).Should().Be("escalated");
        var lineage = await fixture.ReadWorkflowLineageAsync(job.WorkflowInstanceId!.Value);
        lineage.Should().Be(("schedule", "incident_due", job.Id, (long?)null));

        var second = await cycle.RunOnceAsync(fixture.Tenant, "worker_a");
        second.Claimed.Should().Be(0);
        (await fixture.Store.ListJobsAsync(fixture.Tenant)).Should().ContainSingle();
    }

    private static async Task DropRoleAsync(string adminConnectionString, string userName)
    {
        await using var connection = new NpgsqlConnection(
            PostgresConnectionString.Normalize(adminConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"drop role if exists {QuoteIdentifier(userName)}";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class RejectUnexpectedExecution : IRuntimeScheduledWorkflowExecutor
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task<RuntimeScheduledWorkflowExecutionResult> ExecuteAsync(
            ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
            RuntimeScheduledJob job,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("The acceptance fence or missing schema should prevent execution.");
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class ScheduleFixture : IAsyncDisposable
    {
        private readonly string adminConnectionString;
        internal readonly ITenantDatabaseConnectionFactory connectionFactory;
        private readonly string databaseName;

        private ScheduleFixture(
            string adminConnectionString,
            ITenantDatabaseConnectionFactory connectionFactory,
            string databaseName,
            ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
            RuntimeScheduleStore store,
            MutableTimeProvider clock,
            RuntimeScheduleOptions options)
        {
            this.adminConnectionString = adminConnectionString;
            this.connectionFactory = connectionFactory;
            this.databaseName = databaseName;
            Tenant = tenant;
            Store = store;
            Clock = clock;
            Options = options;
        }

        public ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment Tenant { get; }

        public RuntimeScheduleStore Store { get; }

        public ITenantDatabaseConnectionFactory ConnectionFactory => connectionFactory;

        public MutableTimeProvider Clock { get; }

        public RuntimeScheduleOptions Options { get; }

        public IRuntimeScheduledWorkflowExecutor CreateWorkflowExecutor() =>
            new RuntimeScheduledWorkflowExecutor(
                new RuntimeWorkflowStore(
                    connectionFactory,
                    new RuntimePermissionService(connectionFactory),
                    Clock));

        public static async Task<ScheduleFixture> CreateAsync(
            RuntimeScheduleOptions? options = null,
            bool allOperatorSchedules = false)
        {
            var admin = TestPostgres.GetRequiredAdminConnectionString();
            var database = $"erp_tenant_schedule_{Guid.NewGuid():N}"[..48];
            await TestPostgres.CreateDatabaseAsync(admin, database);
            try
            {
                var factory = TestPostgres.CreateConnectionFactory(admin);
                var tenant = RuntimeTestTenant.Create(
                    "schedule",
                    "schedule.erpgenerator.example",
                    database);
                var compiled = new ErpBundleCompiler().Compile(Bundle("1.0.0", "open", allOperatorSchedules));
                await new RuntimeDatabase(factory, new RuntimeSqlBuilder())
                    .ApplyCompiledBundleAsync(tenant, compiled);
                var clock = new MutableTimeProvider(
                    new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
                var effectiveOptions = options ?? new RuntimeScheduleOptions();
                return new ScheduleFixture(
                    admin,
                    factory,
                    database,
                    tenant,
                    new RuntimeScheduleStore(factory, effectiveOptions, clock),
                    clock,
                    effectiveOptions);
            }
            catch
            {
                await TestPostgres.DropDatabaseAsync(admin, database);
                throw;
            }
        }

        public async Task<long> InsertIncidentAsync(string status, DateTimeOffset dueAt)
        {
            await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
                Tenant,
                TenantDatabaseConnectionPurpose.Provisioning));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                insert into app.incidents (title, response_deadline, severity, status)
                values (@title, @dueAt, 'p1', @status)
                returning id
                """;
            command.Parameters.AddWithValue("title", $"Incident {Guid.NewGuid():N}");
            command.Parameters.AddWithValue("dueAt", dueAt);
            command.Parameters.AddWithValue("status", status);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task DeployAsync(string version, string eligibleStatus)
        {
            var compiled = new ErpBundleCompiler().Compile(Bundle(version, eligibleStatus, allOperatorSchedules: false));
            await new RuntimeDatabase(connectionFactory, new RuntimeSqlBuilder())
                .ApplyCompiledBundleAsync(Tenant, compiled);
        }

        public async Task UpdateIncidentAsync(long recordId, string status, DateTimeOffset dueAt)
        {
            await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
                Tenant,
                TenantDatabaseConnectionPurpose.Provisioning));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                update app.incidents
                set status = @status, response_deadline = @dueAt, updated_at = now()
                where id = @recordId
                """;
            command.Parameters.AddWithValue("recordId", recordId);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("dueAt", dueAt);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
                Tenant,
                TenantDatabaseConnectionPurpose.Provisioning));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? default! : (T)result;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
                Tenant,
                TenantDatabaseConnectionPurpose.Provisioning));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> ReadIncidentStatusAsync(long recordId)
        {
            await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
                Tenant,
                TenantDatabaseConnectionPurpose.Provisioning));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "select status from app.incidents where id = @recordId";
            command.Parameters.AddWithValue("recordId", recordId);
            return Convert.ToString(await command.ExecuteScalarAsync())!;
        }

        public async Task<(string TriggerKind, string TriggerKey, long SchedulerJobId, long? StartedBy)>
            ReadWorkflowLineageAsync(long workflowInstanceId)
        {
            await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
                Tenant,
                TenantDatabaseConnectionPurpose.Provisioning));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select trigger_kind, trigger_key, scheduler_job_id, started_by
                from _runtime.workflow_instances
                where id = @workflowInstanceId
                """;
            command.Parameters.AddWithValue("workflowInstanceId", workflowInstanceId);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }

        public async ValueTask DisposeAsync() =>
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);

        internal static ErpBundle Bundle(
            string version,
            string eligibleStatus,
            bool allOperatorSchedules) => new()
            {
                Id = "runtime_schedule_test",
                Name = "Runtime schedule test",
                Version = version,
                Modules =
            [
                new ErpModule
                {
                    Id = "support",
                    Name = "Support",
                    Entities =
                    [
                        new ErpEntity
                        {
                            Id = "incident",
                            Name = "incidents",
                            DisplayName = "Incidents",
                            Fields =
                            [
                                new ErpField
                                {
                                    Id = "title", Name = "title", DisplayName = "Title",
                                    Type = "string", Required = true
                                },
                                new ErpField
                                {
                                    Id = "response_deadline", Name = "response_deadline",
                                    DisplayName = "Response deadline", Type = "datetime", Required = true
                                },
                                new ErpField
                                {
                                    Id = "severity", Name = "severity", DisplayName = "Severity",
                                    Type = "enum", Required = true, Options = ["p1", "p2"]
                                },
                                new ErpField
                                {
                                    Id = "status", Name = "status", DisplayName = "Status",
                                    Type = "enum", Required = true, Options = ["open", "resolved", "escalated"]
                                },
                                new ErpField
                                {
                                    Id = "note", Name = "note", DisplayName = "Note",
                                    Type = "string", Required = false
                                }
                            ]
                        }
                    ],
                    Workflows = allOperatorSchedules
                        ? AllOperatorScheduleIds.Select(id => Workflow($"workflow_{id}")).ToList()
                        : [Workflow("escalate_incident")]
                }
            ],
                Schedules = allOperatorSchedules
                ? AllOperatorSchedules()
                :
                [
                    Schedule(
                        "incident_due",
                        new ErpScheduleFilter
                        {
                            Field = "severity", Op = "eq",
                            Value = StringLiteral("p1")
                        },
                        new ErpScheduleFilter
                        {
                            Field = "status", Op = "eq",
                            Value = StringLiteral(eligibleStatus)
                        })
                ],
                Roles = new Dictionary<string, ErpRole>
                {
                    ["owner"] = new()
                    {
                        DisplayName = "Owner",
                        Grants = [new ErpGrant { Action = "*", Entity = "*" }]
                    }
                }
            };

        private static List<ErpSchedule> AllOperatorSchedules() =>
        [
            Schedule("filter_eq", Scalar("status", "eq", "string", "open")),
            Schedule("filter_neq", Scalar("status", "neq", "string", "resolved")),
            Schedule("filter_in", List("status", "in", "string", "open", "escalated")),
            Schedule("filter_not_in", List("status", "not_in", "string", "resolved")),
            Schedule("filter_gt", Scalar("response_deadline", "gt", "datetime", "2026-07-13T12:00:00.0000000+00:00")),
            Schedule("filter_gte", Scalar("response_deadline", "gte", "datetime", "2026-07-13T13:00:00.0000000+00:00")),
            Schedule("filter_lt", Scalar("response_deadline", "lt", "datetime", "2026-07-13T14:00:00.0000000+00:00")),
            Schedule("filter_lte", Scalar("response_deadline", "lte", "datetime", "2026-07-13T13:00:00.0000000+00:00")),
            Schedule("filter_exists", new ErpScheduleFilter { Field = "title", Op = "exists" }),
            Schedule("filter_not_exists", new ErpScheduleFilter { Field = "note", Op = "not_exists" })
        ];

        private static ErpSchedule Schedule(string id, params ErpScheduleFilter[] filters) => new()
        {
            Id = id,
            Entity = "incident",
            DueField = "response_deadline",
            Workflow = id == "incident_due" ? "escalate_incident" : $"workflow_{id}",
            Filters = [.. filters]
        };

        private static ErpWorkflow Workflow(string id) => new()
        {
            Id = id,
            Name = "Escalate incident",
            Entity = "incident",
            Steps =
            [
                new ErpWorkflowStep
                {
                    Id = "mark_escalated",
                    Type = "update_record",
                    Set = new Dictionary<string, string> { ["status"] = "escalated" }
                }
            ]
        };

        private static readonly string[] AllOperatorScheduleIds =
        [
            "filter_eq", "filter_neq", "filter_in", "filter_not_in",
            "filter_gt", "filter_gte", "filter_lt", "filter_lte",
            "filter_exists", "filter_not_exists"
        ];

        private static ErpScheduleFilter Scalar(
            string field,
            string op,
            string type,
            object value) => new()
            {
                Field = field,
                Op = op,
                Value = new ErpTypedLiteral { Type = type, Value = value }
            };

        private static ErpScheduleFilter List(
            string field,
            string op,
            string type,
            params object[] values) => new()
            {
                Field = field,
                Op = op,
                Values = values.Select(value => new ErpTypedLiteral { Type = type, Value = value }).ToList()
            };

        private static ErpTypedLiteral StringLiteral(string value) => new()
        {
            Type = "string",
            Value = value
        };
    }
}
