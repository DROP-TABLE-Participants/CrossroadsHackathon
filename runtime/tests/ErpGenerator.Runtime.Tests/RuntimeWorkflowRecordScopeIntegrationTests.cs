using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeWorkflowRecordScopeIntegrationTests
{
    [PostgresFact]
    public async Task Workflow_tasks_notifications_counts_and_decisions_are_filtered_by_record_scope_before_limits()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_workflow_record_scope_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "workflow-scope",
                "workflow-scope.erpgenerator.example",
                database);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var workflows = new RuntimeWorkflowStore(factory, permissions);
            await runtimeDatabase.ApplyCompiledBundleAsync(
                tenant,
                new ErpBundleCompiler().Compile(Bundle()));

            var alice = Worker(1101, "alice", "worker", "blind_worker", "workflow_approver");
            var bob = Worker(1102, "bob");
            var blindWorker = Worker(1103, "blind", "blind_worker");
            var systemOwner = new RuntimeUserContext(
                1,
                "system@workflow-scope.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };

            var aliceRequest = await records.CreateRecordAsync(
                tenant,
                alice,
                "request",
                RuntimeJson.JsonObject(
                    ("name", $"Alice request {suffix}"),
                    ("approval_status", "draft")));
            var bobRequest = await records.CreateRecordAsync(
                tenant,
                bob,
                "request",
                RuntimeJson.JsonObject(
                    ("name", $"Bob request {suffix}"),
                    ("approval_status", "draft")));
            var aliceRequestId = Convert.ToInt64(aliceRequest["id"]);
            var bobRequestId = Convert.ToInt64(bobRequest["id"]);

            (await workflows.ListRecordActionsAsync(tenant, alice, "request", aliceRequestId))
                .Should().ContainSingle(action => action.Key == "submit_for_approval");
            (await workflows.ListRecordActionsAsync(tenant, bob, "request", aliceRequestId))
                .Should().BeEmpty();
            (await workflows.ListRecordActionsAsync(tenant, blindWorker, "request", aliceRequestId))
                .Should().BeEmpty();
            await FluentActions.Awaiting(() => workflows.StartActionAsync(
                    tenant,
                    bob,
                    "request",
                    aliceRequestId,
                    "submit_for_approval"))
                .Should().ThrowAsync<RuntimeWorkflowRecordNotFoundException>();
            await FluentActions.Awaiting(() => workflows.StartActionAsync(
                    tenant,
                    blindWorker,
                    "request",
                    aliceRequestId,
                    "submit_for_approval"))
                .Should().ThrowAsync<RuntimeWorkflowPermissionException>();

            var started = await workflows.StartActionAsync(
                tenant,
                alice,
                "request",
                aliceRequestId,
                "submit_for_approval");
            started.Task.Should().NotBeNull();
            var taskId = started.Task!.Id;

            (await workflows.ListTasksAsync(tenant, alice)).Should().ContainSingle(task => task.Id == taskId);
            (await workflows.ListTasksAsync(tenant, bob)).Should().BeEmpty();
            (await workflows.ListTasksAsync(tenant, blindWorker)).Should().BeEmpty();
            (await workflows.CountPendingTasksAsync(tenant, alice)).Should().Be(1);
            (await workflows.CountPendingTasksAsync(tenant, bob)).Should().Be(0);
            (await workflows.CountPendingTasksAsync(tenant, blindWorker)).Should().Be(0);
            (await workflows.ListInstancesAsync(tenant, alice)).Should().ContainSingle(instance => instance.Id == started.Instance.Id);
            (await workflows.ListInstancesAsync(tenant, bob)).Should().BeEmpty();
            (await workflows.ListInstancesAsync(tenant, blindWorker)).Should().BeEmpty();
            (await workflows.CountRunningInstancesAsync(tenant, alice)).Should().Be(1);
            (await workflows.CountRunningInstancesAsync(tenant, bob)).Should().Be(0);
            (await workflows.CountRunningInstancesAsync(tenant, blindWorker)).Should().Be(0);
            (await workflows.ListRecentEventsAsync(tenant, alice)).Should().NotBeEmpty();
            (await workflows.ListRecentEventsAsync(tenant, bob)).Should().BeEmpty();
            (await workflows.ListRecentEventsAsync(tenant, blindWorker)).Should().BeEmpty();
            var aliceNotification = (await workflows.ListNotificationsAsync(tenant, alice))
                .Should().ContainSingle(notification => notification.TaskId == taskId).Subject;
            (await workflows.ListNotificationsAsync(tenant, bob)).Should().BeEmpty();
            (await workflows.ListNotificationsAsync(tenant, blindWorker)).Should().BeEmpty();
            (await workflows.CountUnreadNotificationsAsync(tenant, alice)).Should().Be(1);
            (await workflows.CountUnreadNotificationsAsync(tenant, bob)).Should().Be(0);
            (await workflows.CountUnreadNotificationsAsync(tenant, blindWorker)).Should().Be(0);
            (await workflows.GetTaskAsync(tenant, bob, taskId)).Should().BeNull();
            (await workflows.GetInstanceAsync(tenant, bob, started.Instance.Id)).Should().BeNull();
            await FluentActions.Awaiting(() => workflows.GetTaskAsync(tenant, blindWorker, taskId))
                .Should().ThrowAsync<RuntimeWorkflowPermissionException>();
            await FluentActions.Awaiting(() => workflows.GetInstanceAsync(tenant, blindWorker, started.Instance.Id))
                .Should().ThrowAsync<RuntimeWorkflowPermissionException>();
            await FluentActions.Awaiting(() => workflows.ApproveTaskAsync(tenant, bob, taskId))
                .Should().ThrowAsync<RuntimeWorkflowTaskNotFoundException>();
            await FluentActions.Awaiting(() => workflows.ApproveTaskAsync(tenant, blindWorker, taskId))
                .Should().ThrowAsync<RuntimeWorkflowPermissionException>();
            (await workflows.MarkNotificationReadAsync(tenant, blindWorker, aliceNotification.Id)).Should().BeFalse();

            var approved = await workflows.ApproveTaskAsync(tenant, alice, taskId);
            approved.Instance.Status.Should().Be("completed");
            var finalAlice = await records.GetRecordAsync(tenant, systemOwner, "request", aliceRequestId);
            var finalBob = await records.GetRecordAsync(tenant, systemOwner, "request", bobRequestId);
            finalAlice!["approval_status"].Should().Be("approved");
            finalBob!["approval_status"].Should().Be("draft");
            (await workflows.ListTasksAsync(tenant, bob, includeCompleted: true)).Should().BeEmpty();

            for (var index = 0; index < 101; index++)
            {
                var newerRequest = await records.CreateRecordAsync(
                    tenant,
                    alice,
                    "request",
                    RuntimeJson.JsonObject(
                        ("name", $"Newer request {index:D3} {suffix}"),
                        ("approval_status", "draft")));
                await workflows.StartActionAsync(
                    tenant,
                    alice,
                    "request",
                    Convert.ToInt64(newerRequest["id"]),
                    "submit_for_approval");
            }

            (await workflows.ListInstancesAsync(tenant, alice))
                .Should().HaveCount(100)
                .And.NotContain(instance => instance.Id == started.Instance.Id);
            (await workflows.ListRecordInstancesAsync(tenant, alice, "request", aliceRequestId))
                .Should().ContainSingle(instance => instance.Id == started.Instance.Id);
            (await workflows.ListRecordInstancesAsync(tenant, bob, "request", aliceRequestId))
                .Should().BeEmpty();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Persisted_user_with_zero_durable_roles_cannot_reuse_stale_context_roles_or_task_assignment()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_workflow_role_revoke_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "workflow-revoke",
                "workflow-revoke.erpgenerator.example",
                database);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var workflows = new RuntimeWorkflowStore(factory, permissions);
            var users = new RuntimeUserStore(factory);
            await runtimeDatabase.ApplyCompiledBundleAsync(
                tenant,
                new ErpBundleCompiler().Compile(Bundle()));

            var inviter = new RuntimeUserContext(
                -1,
                "system@workflow-revoke.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var email = $"persisted-{suffix}@workflow-revoke.test";
            var invite = await users.InviteUserAsync(
                tenant,
                inviter,
                email,
                "Persisted worker",
                ["worker", "blind_worker", "workflow_approver"],
                employeeRecordId: null,
                lifetime: TimeSpan.FromDays(1));
            (await users.ActivateInvitedUserAsync(
                tenant,
                invite.ActivationToken!,
                email,
                "Persisted worker",
                "RuntimePass123!"))
                .Should().BeTrue();
            var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, "RuntimePass123!");
            login.Should().NotBeNull();
            var staleUser = new RuntimeUserContext(login!.Id, login.Email, login.Role)
            {
                Roles = login.Roles
            };

            var request = await records.CreateRecordAsync(
                tenant,
                staleUser,
                "request",
                RuntimeJson.JsonObject(
                    ("name", $"Persisted request {suffix}"),
                    ("approval_status", "draft")));
            var requestId = Convert.ToInt64(request["id"]);
            var started = await workflows.StartActionAsync(
                tenant,
                staleUser,
                "request",
                requestId,
                "submit_for_approval");
            (await workflows.ListTasksAsync(tenant, staleUser))
                .Should().ContainSingle(task => task.Id == started.Task!.Id);

            await using (var connection = new NpgsqlConnection(
                             PostgresConnectionString.Normalize(adminConnectionString, database)))
            {
                await connection.OpenAsync();
                await using var revoke = new NpgsqlCommand(
                    "delete from _runtime.runtime_user_roles where user_id = @userId",
                    connection);
                revoke.Parameters.AddWithValue("userId", staleUser.UserId);
                (await revoke.ExecuteNonQueryAsync()).Should().Be(3);
            }

            staleUser.Roles.Should().Contain("worker");
            staleUser.Roles.Should().Contain("blind_worker");
            staleUser.Roles.Should().Contain("workflow_approver");
            var entity = await records.GetEntityAsync(tenant, "request");
            entity.Should().NotBeNull();
            (await permissions.IsAllowedAsync(tenant, staleUser, "read", entity)).Should().BeFalse();
            (await workflows.ListTasksAsync(tenant, staleUser)).Should().BeEmpty();
            (await workflows.CountPendingTasksAsync(tenant, staleUser)).Should().Be(0);
            await FluentActions.Awaiting(() => workflows.ApproveTaskAsync(
                    tenant,
                    staleUser,
                    started.Task!.Id))
                .Should().ThrowAsync<RuntimeWorkflowPermissionException>();

            await using (var connection = new NpgsqlConnection(
                             PostgresConnectionString.Normalize(adminConnectionString, database)))
            {
                await connection.OpenAsync();
                await using var disable = new NpgsqlCommand(
                    """
                    insert into _runtime.runtime_user_roles (user_id, role_id)
                    values (@userId, 'worker'), (@userId, 'blind_worker'), (@userId, 'workflow_approver');
                    update _runtime.runtime_users set status = 'disabled' where id = @userId;
                    """,
                    connection);
                disable.Parameters.AddWithValue("userId", staleUser.UserId);
                await disable.ExecuteNonQueryAsync();
            }

            (await permissions.IsAllowedAsync(tenant, staleUser, "read", entity)).Should().BeFalse();
            (await workflows.ListTasksAsync(tenant, staleUser)).Should().BeEmpty();
            await FluentActions.Awaiting(() => workflows.ApproveTaskAsync(
                    tenant,
                    staleUser,
                    started.Task!.Id))
                .Should().ThrowAsync<RuntimeWorkflowPermissionException>();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static RuntimeUserContext Worker(long id, string name, params string[] roles) =>
        new(id, $"{name}@workflow-scope.test", roles.FirstOrDefault() ?? "worker")
        {
            Roles = roles.Length == 0 ? ["worker"] : roles
        };

    private static ErpBundle Bundle() => new()
    {
        Id = "workflow_record_scope",
        Name = "Workflow record scope",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "operations",
                Name = "Operations",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "request",
                        Name = "requests",
                        DisplayName = "Request",
                        Display = new ErpEntityDisplay { Primary = "name", Secondary = "approval_status" },
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name",
                                Name = "name",
                                DisplayName = "Name",
                                Type = "string",
                                Required = true
                            },
                            new ErpField
                            {
                                Id = "approval_status",
                                Name = "approval_status",
                                DisplayName = "Approval status",
                                Type = "enum",
                                Required = true,
                                Options = ["draft", "submitted", "approved", "rejected"]
                            }
                        ]
                    }
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = "request_approval",
                        Name = "Request approval",
                        Entity = "request",
                        States = ["draft", "submitted", "approved", "rejected"],
                        Trigger = new ErpWorkflowTrigger { Action = "submit_for_approval" },
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "mark_submitted",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["approval_status"] = "submitted" }
                            },
                            new ErpWorkflowStep
                            {
                                Id = "worker_approval",
                                Type = "approval",
                                Label = "Workflow approval",
                                AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "workflow_approver" },
                                ApproveLabel = "Approve",
                                RejectLabel = "Reject"
                            },
                            new ErpWorkflowStep
                            {
                                Id = "mark_approved",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["approval_status"] = "approved" }
                            }
                        ],
                        OnRejected =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "mark_rejected",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["approval_status"] = "rejected" }
                            }
                        ]
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["worker"] = new ErpRole
            {
                DisplayName = "Worker",
                Grants =
                [
                    RecordGrant("read"),
                    RecordGrant("create"),
                    RecordGrant("start_workflow"),
                    RecordGrant("approve_task"),
                    RecordGrant("reject_task"),
                    new ErpGrant { Action = "read_task", Entity = "request" }
                ]
            },
            ["blind_worker"] = new ErpRole
            {
                DisplayName = "Blind worker",
                Grants =
                [
                    new ErpGrant { Action = "start_workflow", Entity = "request" },
                    new ErpGrant { Action = "approve_task", Entity = "request" },
                    new ErpGrant { Action = "reject_task", Entity = "request" },
                    new ErpGrant { Action = "read_task", Entity = "request" }
                ]
            },
            ["workflow_approver"] = new ErpRole
            {
                DisplayName = "Workflow approver",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "request" },
                    new ErpGrant { Action = "read_task", Entity = "request" },
                    new ErpGrant { Action = "approve_task", Entity = "request" },
                    new ErpGrant { Action = "reject_task", Entity = "request" }
                ]
            }
        }
    };

    private static ErpGrant RecordGrant(string action) => new()
    {
        Scope = PermissionGrantScopes.Record,
        Action = action,
        Entity = "request",
        RecordScope = new ErpRecordScope
        {
            Field = "created_by",
            Source = RecordPermissionScopeSources.ActorPrincipal
        }
    };
}
