using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeUserManagementIntegrationTests
{
    [PostgresFact]
    public async Task Regenerated_owner_activation_token_supersedes_pending_token()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_owner_activation_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var email = $"owner-{suffix}@example.test";
            var firstToken = await runtimeDatabase.CreateActivationTokenAsync(tenant, email, TimeSpan.FromDays(7));
            (await RuntimeDatabaseProbe.ActivationTokenHashExistsAsync(adminConnectionString, databaseName, firstToken)).Should().BeTrue();

            (await runtimeDatabase.ExpirePendingOwnerActivationTokensAsync(tenant, email)).Should().Be(1);
            var secondToken = await runtimeDatabase.CreateActivationTokenAsync(tenant, email, TimeSpan.FromDays(7));

            (await runtimeDatabase.ActivateOwnerAsync(tenant, firstToken, email, "Owner", "RuntimePass123!")).Should().BeFalse();
            (await runtimeDatabase.ActivateOwnerAsync(
                tenant,
                secondToken,
                $"attacker-{suffix}@example.test",
                "Wrong Owner",
                "RuntimePass123!")).Should().BeFalse();
            (await runtimeDatabase.ActivateOwnerAsync(tenant, secondToken, email, "Owner", "RuntimePass123!")).Should().BeTrue();
            (await runtimeDatabase.ActivateOwnerAsync(tenant, secondToken, email, "Owner", "RuntimePass123!")).Should().BeFalse();
            var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, "RuntimePass123!");
            login.Should().NotBeNull();
            login!.Role.Should().Be(RuntimeSystemRoleIds.TenantOwner);
            login.Roles.Should().Equal(RuntimeSystemRoleIds.TenantOwner);

            await using (var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString, databaseName)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    update _runtime.runtime_users set role = 'owner' where id = @userId;
                    delete from _runtime.runtime_user_roles where user_id = @userId;
                    insert into _runtime.runtime_user_roles (user_id, role_id) values (@userId, 'owner');
                    """;
                command.Parameters.AddWithValue("userId", login.Id);
                await command.ExecuteNonQueryAsync();
            }

            var migratedLogin = await runtimeDatabase.ValidateLoginAsync(tenant, email, "RuntimePass123!");
            migratedLogin.Should().NotBeNull();
            migratedLogin!.Role.Should().Be(RuntimeSystemRoleIds.TenantOwner);
            migratedLogin.Roles.Should().Equal(RuntimeSystemRoleIds.TenantOwner);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Owner_activation_repairs_missing_tenant_owner_system_role()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_owner_role_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            await using (var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString, databaseName)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "delete from _runtime.runtime_roles";
                await command.ExecuteNonQueryAsync();
            }

            var email = $"owner-role-{suffix}@example.test";
            var token = await runtimeDatabase.CreateActivationTokenAsync(tenant, email, TimeSpan.FromDays(7));

            (await runtimeDatabase.ActivateOwnerAsync(tenant, token, email, "Owner", "RuntimePass123!")).Should().BeTrue();

            var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, "RuntimePass123!");
            login.Should().NotBeNull();
            login!.Role.Should().Be(RuntimeSystemRoleIds.TenantOwner);
            login.Roles.Should().Contain(RuntimeSystemRoleIds.TenantOwner);
            login.Roles.Should().NotContain("owner");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Runtime_user_invite_activation_roles_disable_and_employee_linking_work_per_tenant()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_users_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var users = new RuntimeUserStore(factory);
            var records = new RuntimeRecordStore(factory);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, tenant, "owner@acme.test");

            (await users.ListRolesAsync(tenant)).Should().NotContain(role =>
                RuntimeSystemRoleIds.IsReserved(role.Id));

            var inviteReservedRole = () => users.InviteUserAsync(
                tenant,
                owner,
                $"forged-owner-{suffix}@example.test",
                "Forged Owner",
                [RuntimeSystemRoleIds.TenantOwner],
                null,
                TimeSpan.FromDays(7));
            await inviteReservedRole.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("*reserved*cannot be assigned*");

            var updateReservedRole = () => users.UpdateUserAsync(
                tenant,
                owner,
                owner.UserId,
                displayName: null,
                roles: [RuntimeSystemRoleIds.TenantOwner]);
            await updateReservedRole.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("*reserved*cannot be assigned*");

            var addReservedRole = () => users.AddRoleAsync(
                tenant,
                owner,
                owner.UserId,
                RuntimeSystemRoleIds.TenantOwner);
            await addReservedRole.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("*reserved*cannot be assigned*");

            var removeReservedRole = () => users.RemoveRoleAsync(
                tenant,
                owner,
                owner.UserId,
                RuntimeSystemRoleIds.TenantOwner);
            await removeReservedRole.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("*reserved*cannot be assigned or removed*");

            var inviteWithoutRole = () => users.InviteUserAsync(
                tenant,
                owner,
                $"unscoped-{suffix}@example.test",
                "Unscoped User",
                [],
                null,
                TimeSpan.FromDays(7));
            await inviteWithoutRole.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("*explicit runtime role*");

            var employee = await records.CreateRecordAsync(tenant, owner, "employees", RuntimeJson.JsonObject(
                ("full_name", $"PM {suffix}"),
                ("email", $"pm-{suffix}@example.test"),
                ("role", "Project Manager")));
            var invite = await users.InviteUserAsync(
                tenant,
                owner,
                $"pm-{suffix}@example.test",
                $"PM {suffix}",
                ["member"],
                Convert.ToInt64(employee["id"]),
                TimeSpan.FromDays(7));

            invite.ActivationToken.Should().NotBeNullOrWhiteSpace();
            (await RuntimeDatabaseProbe.ActivationTokenHashExistsAsync(adminConnectionString, databaseName, invite.ActivationToken!)).Should().BeTrue();
            (await users.ListUsersAsync(tenant)).Should().Contain(user =>
                user.Email == $"pm-{suffix}@example.test" &&
                user.Status == "pending" &&
                user.Roles.Contains("member") &&
                user.LinkedEmployee!.RecordId == Convert.ToInt64(employee["id"]));

            (await users.ActivateInvitedUserAsync(tenant, invite.ActivationToken!, $"pm-{suffix}@example.test", $"PM {suffix}", "ProjectPass123!")).Should().BeTrue();
            (await users.ActivateInvitedUserAsync(tenant, invite.ActivationToken!, $"pm-{suffix}@example.test", $"PM {suffix}", "ProjectPass123!")).Should().BeFalse();
            var pmLogin = await runtimeDatabase.ValidateLoginAsync(tenant, $"pm-{suffix}@example.test", "ProjectPass123!");
            pmLogin.Should().NotBeNull();
            pmLogin!.Roles.Should().Contain("member");

            (await users.AddRoleAsync(tenant, owner, pmLogin.Id, "admin")).Should().BeTrue();
            (await users.GetActiveUserContextAsync(tenant, pmLogin.Id))!.Roles.Should().Contain("admin");
            (await users.RemoveRoleAsync(tenant, owner, pmLogin.Id, "admin")).Should().BeTrue();
            (await users.GetActiveUserContextAsync(tenant, pmLogin.Id))!.Roles.Should().NotContain("admin");

            (await users.SetUserEnabledAsync(tenant, owner, pmLogin.Id, enabled: false)).Should().BeTrue();
            (await runtimeDatabase.ValidateLoginAsync(tenant, $"pm-{suffix}@example.test", "ProjectPass123!")).Should().BeNull();
            (await users.GetActiveUserContextAsync(tenant, pmLogin.Id)).Should().BeNull();
            (await users.SetUserEnabledAsync(tenant, owner, pmLogin.Id, enabled: true)).Should().BeTrue();
            (await runtimeDatabase.ValidateLoginAsync(tenant, $"pm-{suffix}@example.test", "ProjectPass123!")).Should().NotBeNull();
            (await users.UnlinkEmployeeAsync(tenant, owner, pmLogin.Id)).Should().BeTrue();

            var auditEvents = await records.ListAuditEventsAsync(tenant, 100);
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.invited");
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.activated");
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.disabled");
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.enabled");
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.role_added");
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.role_removed");
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.linked_employee");
            auditEvents.Should().Contain(@event => @event.EventType == "runtime_user.unlinked_employee");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Generated_owner_role_uses_explicit_bundle_grants_and_is_not_promoted_to_system_owner()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_generated_owner_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var users = new RuntimeUserStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, await RuntimeBundleTestHelper.CompileSampleBundleAsync());

            var systemOwner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, tenant, $"bootstrap-{suffix}@example.test");
            var generatedOwnerEmail = $"business-owner-{suffix}@example.test";
            var invite = await users.InviteUserAsync(
                tenant,
                systemOwner,
                generatedOwnerEmail,
                "Business Owner",
                ["owner"],
                null,
                TimeSpan.FromDays(7));
            invite.ActivationToken.Should().NotBeNullOrWhiteSpace();
            (await users.ActivateInvitedUserAsync(
                tenant,
                invite.ActivationToken!,
                generatedOwnerEmail,
                "Business Owner",
                "BusinessOwnerPass123!")).Should().BeTrue();

            await using (var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString, databaseName)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    update _runtime.role_definitions
                    set grants = '[{"Action":"read","Entity":"customer"}]'::jsonb
                    where id = 'owner'
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var login = await runtimeDatabase.ValidateLoginAsync(tenant, generatedOwnerEmail, "BusinessOwnerPass123!");
            login.Should().NotBeNull();
            login!.Role.Should().Be("owner");
            login.Roles.Should().Equal("owner");
            var generatedOwner = new RuntimeUserContext(login.Id, login.Email, login.Role) { Roles = login.Roles };
            var customer = await new RuntimeRecordStore(factory).GetEntityAsync(tenant, "customer");
            customer.Should().NotBeNull();

            (await permissions.IsAllowedAsync(tenant, generatedOwner, "read", customer)).Should().BeTrue();
            (await permissions.IsAllowedAsync(tenant, generatedOwner, "create", customer)).Should().BeFalse();
            (await permissions.IsAllowedAsync(tenant, systemOwner, "create", customer)).Should().BeTrue();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Timesheet_approval_assigns_to_project_manager_user_and_falls_back_to_owner()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeDatabase = $"erp_tenant_pm_acme_{suffix}";
        var betaDatabase = $"erp_tenant_pm_beta_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, acmeDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, betaDatabase);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var users = new RuntimeUserStore(factory);
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            var acme = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", acmeDatabase);
            var beta = RuntimeTestTenant.Create("beta", "beta.erpgenerator.example", betaDatabase);
            await runtimeDatabase.ApplyCompiledBundleAsync(acme, compiled);
            await runtimeDatabase.ApplyCompiledBundleAsync(beta, compiled);

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, acme, "owner@acme.test");
            var betaOwner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, beta, "owner@beta.test");
            var pm = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(users, runtimeDatabase, acme, owner, $"pm-{suffix}@example.test", name: $"pm-{suffix}@example.test");
            var randomMember = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(users, runtimeDatabase, acme, owner, $"random-{suffix}@example.test", name: $"random-{suffix}@example.test");

            var pmEmployee = await records.CreateRecordAsync(acme, owner, "employees", RuntimeJson.JsonObject(
                ("full_name", $"PM {suffix}"),
                ("email", $"pm-{suffix}@example.test"),
                ("role", "Project Manager")));
            var workerEmployee = await records.CreateRecordAsync(acme, owner, "employees", RuntimeJson.JsonObject(
                ("full_name", $"Worker {suffix}"),
                ("email", $"worker-{suffix}@example.test"),
                ("role", "Consultant")));
            var customer = await records.CreateRecordAsync(acme, owner, "customers", RuntimeJson.JsonObject(
                ("company_name", $"Customer {suffix}"),
                ("contact_email", $"customer-{suffix}@example.test"),
                ("status", "active")));
            var project = await records.CreateRecordAsync(acme, owner, "projects", RuntimeJson.JsonObject(
                ("customer_id", customer["id"]!),
                ("project_manager_id", pmEmployee["id"]!),
                ("title", $"Project {suffix}"),
                ("budget", 1000),
                ("starts_on", "2026-07-02")));
            var timesheet = await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", workerEmployee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-02"),
                ("hours", 6.5),
                ("approval_status", "draft")));

            var start = await workflows.StartActionAsync(acme, owner, "timesheets", Convert.ToInt64(timesheet["id"]), "submit_for_approval");
            start.Task!.AssignedUserId.Should().Be(pm.UserId);
            start.Task.AssignedRole.Should().BeNull();
            start.Task.Label.Should().Be("Project Manager Approval");
            (await workflows.ListTasksAsync(acme, pm)).Should().ContainSingle(task => task.Id == start.Task.Id);
            (await workflows.ListTasksAsync(acme, owner)).Should().ContainSingle(task => task.Id == start.Task.Id);
            (await workflows.ListTasksAsync(acme, randomMember)).Should().BeEmpty();
            (await workflows.CountUnreadNotificationsAsync(acme, pm)).Should().Be(1);
            var pmNotifications = await workflows.ListNotificationsAsync(acme, pm);
            var pmTaskNotification = pmNotifications.Should().ContainSingle(notification =>
                notification.TaskId == start.Task.Id &&
                notification.RecipientUserId == pm.UserId &&
                notification.Status == "unread").Subject;
            (await workflows.ListNotificationsAsync(acme, randomMember)).Should().BeEmpty();
            (await workflows.MarkNotificationReadAsync(acme, pm, pmTaskNotification.Id)).Should().BeTrue();
            (await workflows.CountUnreadNotificationsAsync(acme, pm)).Should().Be(0);
            var randomApprove = () => workflows.ApproveTaskAsync(acme, randomMember, start.Task.Id);
            await randomApprove.Should().ThrowAsync<RuntimeWorkflowPermissionException>();

            (await users.AddRoleAsync(acme, owner, randomMember.UserId, "admin")).Should().BeTrue();
            var admin = await users.GetActiveUserContextAsync(acme, randomMember.UserId);
            admin.Should().NotBeNull();
            admin!.Roles.Should().Contain("admin");
            (await workflows.ListTasksAsync(acme, admin)).Should().ContainSingle(task => task.Id == start.Task.Id);

            var adminTimesheet = await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", workerEmployee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-03"),
                ("hours", 4),
                ("approval_status", "draft")));
            var adminManagedStart = await workflows.StartActionAsync(acme, owner, "timesheets", Convert.ToInt64(adminTimesheet["id"]), "submit_for_approval");
            adminManagedStart.Task!.AssignedUserId.Should().Be(pm.UserId);
            (await workflows.ApproveTaskAsync(acme, admin, adminManagedStart.Task.Id)).Instance.Status.Should().Be("completed");

            var approved = await workflows.ApproveTaskAsync(acme, pm, start.Task.Id);
            approved.Instance.Status.Should().Be("completed");
            (await records.GetRecordAsync(acme, "timesheets", Convert.ToInt64(timesheet["id"])))!["approval_status"].Should().Be("approved");

            var overtimeTimesheet = await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", workerEmployee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-05"),
                ("hours", 10),
                ("approval_status", "draft")));
            var overtimeStart = await workflows.StartActionAsync(acme, owner, "timesheets", Convert.ToInt64(overtimeTimesheet["id"]), "submit_for_approval");
            overtimeStart.Task!.AssignedUserId.Should().Be(pm.UserId);
            var afterPmApproval = await workflows.ApproveTaskAsync(acme, pm, overtimeStart.Task.Id);
            afterPmApproval.Instance.Status.Should().Be("waiting");
            var ownerOvertimeTask = (await workflows.ListTasksAsync(acme, owner)).Should().ContainSingle(task =>
                task.InstanceId == overtimeStart.Instance.Id &&
                task.StepId == "request_owner_overtime_approval" &&
                task.Status == "pending").Subject;
            (await workflows.ListTasksAsync(acme, pm)).Should().NotContain(task => task.Id == ownerOvertimeTask.Id);
            var pmOvertimeApprove = () => workflows.ApproveTaskAsync(acme, pm, ownerOvertimeTask.Id);
            await pmOvertimeApprove.Should().ThrowAsync<RuntimeWorkflowPermissionException>();
            (await workflows.ListNotificationsAsync(acme, owner)).Should().Contain(notification =>
                notification.TaskId == ownerOvertimeTask.Id &&
                notification.RecipientRole == "owner" &&
                notification.Status == "unread");
            var finalOvertimeApproval = await workflows.ApproveTaskAsync(acme, owner, ownerOvertimeTask.Id);
            finalOvertimeApproval.Instance.Status.Should().Be("completed");
            (await records.GetRecordAsync(acme, "timesheets", Convert.ToInt64(overtimeTimesheet["id"])))!["approval_status"].Should().Be("approved");
            (await workflows.GetInstanceAsync(acme, owner, overtimeStart.Instance.Id))!.Events.Should().Contain(@event =>
                @event.EventType == "workflow.branch.entered" &&
                @event.Metadata.GetProperty("conditionId").GetString() == "overtime_owner_review");

            var acmeNotificationsBeforeBeta = (await workflows.ListNotificationsAsync(acme, owner))
                .OrderBy(notification => notification.Id)
                .Select(notification => new
                {
                    notification.Id,
                    notification.InstanceId,
                    notification.TaskId,
                    notification.NotificationType,
                    notification.RecipientRole,
                    notification.RecipientUserId,
                    notification.Status,
                    notification.ReadAt,
                    notification.ReadBy
                })
                .ToArray();

            var betaTimesheetId = await RuntimeRecordTestHelper.CreateDraftTimesheetAsync(beta, records, betaOwner, $"{suffix}-beta");
            var betaStart = await workflows.StartActionAsync(beta, betaOwner, "timesheets", betaTimesheetId, "submit_for_approval");
            betaStart.Task!.AssignedRole.Should().Be("owner");
            betaStart.Task.AssignedUserId.Should().BeNull();
            (await workflows.ListTasksAsync(beta, betaOwner)).Should().ContainSingle(task => task.Id == betaStart.Task.Id);
            (await workflows.ListNotificationsAsync(beta, betaOwner)).Should().ContainSingle(notification => notification.TaskId == betaStart.Task.Id);
            var acmeNotificationsAfterBeta = (await workflows.ListNotificationsAsync(acme, owner))
                .OrderBy(notification => notification.Id)
                .Select(notification => new
                {
                    notification.Id,
                    notification.InstanceId,
                    notification.TaskId,
                    notification.NotificationType,
                    notification.RecipientRole,
                    notification.RecipientUserId,
                    notification.Status,
                    notification.ReadAt,
                    notification.ReadBy
                })
                .ToArray();
            acmeNotificationsAfterBeta.Should().HaveCount(acmeNotificationsBeforeBeta.Length);
            acmeNotificationsAfterBeta.Should().Equal(acmeNotificationsBeforeBeta);

            var missingRelationTimesheet = await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", workerEmployee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-04"),
                ("hours", 3),
                ("approval_status", "draft")));
            await PointTimesheetAtMissingProjectAsync(factory, acme, Convert.ToInt64(missingRelationTimesheet["id"]));
            var missingRelationStart = await workflows.StartActionAsync(acme, owner, "timesheets", Convert.ToInt64(missingRelationTimesheet["id"]), "submit_for_approval");
            missingRelationStart.Task!.AssignedRole.Should().Be("owner");
            missingRelationStart.Task.AssignedUserId.Should().BeNull();
            (await workflows.ListTasksAsync(acme, pm)).Should().BeEmpty();

            var auditEvents = await records.ListAuditEventsAsync(acme, 100);
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.task.assigned");
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.task.assignment_fallback");
            var betaAuditEvents = await records.ListAuditEventsAsync(beta, 100);
            betaAuditEvents.Should().Contain(@event => @event.EventType == "workflow.task.assignment_fallback");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
        }
    }

    private static async Task PointTimesheetAtMissingProjectAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        long timesheetId)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update app.timesheets
            set project_id = 999999
            where id = @timesheetId
            """;
        command.Parameters.AddWithValue("timesheetId", timesheetId);
        await command.ExecuteNonQueryAsync();
    }

}
