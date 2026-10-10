using FluentAssertions;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeWorkflowIntegrationTests
{
    [PostgresFact]
    public async Task Timesheet_approval_workflow_updates_records_events_tasks_and_stays_tenant_isolated()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeDatabase = $"erp_tenant_workflow_acme_{suffix}";
        var betaDatabase = $"erp_tenant_workflow_beta_{suffix}";

        await TestPostgres.CreateDatabaseAsync(adminConnectionString, acmeDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, betaDatabase);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();

            var acme = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", acmeDatabase);
            var beta = RuntimeTestTenant.Create("beta", "beta.erpgenerator.example", betaDatabase);

            await runtimeDatabase.ApplyCompiledBundleAsync(acme, compiled);
            await runtimeDatabase.ApplyCompiledBundleAsync(beta, compiled);

            var owner = new RuntimeUserContext(1, "owner@acme.test", "owner");
            var member = new RuntimeUserContext(2, "member@acme.test", "member");
            var betaOwner = new RuntimeUserContext(1, "owner@beta.test", "owner");

            var acmeTimesheetId = await RuntimeRecordTestHelper.CreateDraftTimesheetAsync(acme, records, owner, suffix);
            var betaTimesheetId = await RuntimeRecordTestHelper.CreateDraftTimesheetAsync(beta, records, betaOwner, suffix);

            var draftActions = await workflows.ListRecordActionsAsync(acme, owner, "timesheets", acmeTimesheetId);
            draftActions.Should().ContainSingle(action => action.Key == "submit_for_approval");

            var start = await workflows.StartActionAsync(acme, owner, "timesheets", acmeTimesheetId, "submit_for_approval");
            start.Instance.WorkflowId.Should().Be("timesheet_approval");
            start.Instance.Status.Should().Be("waiting");
            start.Task.Should().NotBeNull();
            start.Task!.Status.Should().Be("pending");
            start.Task.AssignedRole.Should().Be("owner");
            start.Task.CanReject.Should().BeTrue("the workflow declares an onRejected branch");
            (await workflows.ListNotificationsAsync(acme, owner)).Should().ContainSingle(notification =>
                notification.TaskId == start.Task.Id &&
                notification.NotificationType == "task_assigned" &&
                notification.Status == "unread");

            var submitted = await records.GetRecordAsync(acme, "timesheets", acmeTimesheetId);
            submitted!["approval_status"].Should().Be("submitted");

            var acmeTasks = await workflows.ListTasksAsync(acme, owner);
            acmeTasks.Should().ContainSingle(task => task.Id == start.Task.Id);
            (await workflows.ListTasksAsync(beta, betaOwner)).Should().BeEmpty();

            var duplicateStart = () => workflows.StartActionAsync(acme, owner, "timesheets", acmeTimesheetId, "submit_for_approval");
            await duplicateStart.Should().ThrowAsync<RuntimeWorkflowConflictException>();

            var unauthorizedApprove = () => workflows.ApproveTaskAsync(acme, member, start.Task.Id);
            await unauthorizedApprove.Should().ThrowAsync<RuntimeWorkflowPermissionException>();

            var approved = await workflows.ApproveTaskAsync(acme, owner, start.Task.Id, "looks correct");
            approved.Instance.Status.Should().Be("completed");
            approved.Task.Status.Should().Be("approved");

            var approvedRecord = await records.GetRecordAsync(acme, "timesheets", acmeTimesheetId);
            approvedRecord!["approval_status"].Should().Be("approved");

            var duplicateApprove = () => workflows.ApproveTaskAsync(acme, owner, start.Task.Id);
            await duplicateApprove.Should().ThrowAsync<RuntimeWorkflowConflictException>();

            (await workflows.ListRecordActionsAsync(acme, owner, "timesheets", acmeTimesheetId)).Should().BeEmpty();
            var resubmitApproved = () => workflows.StartActionAsync(acme, owner, "timesheets", acmeTimesheetId, "submit_for_approval");
            await resubmitApproved.Should().ThrowAsync<RuntimeWorkflowConflictException>();

            var rejectTimesheetId = await RuntimeRecordTestHelper.CreateDraftTimesheetAsync(acme, records, owner, $"{suffix}-reject");
            var rejectStart = await workflows.StartActionAsync(acme, owner, "timesheets", rejectTimesheetId, "submit_for_approval");
            var rejected = await workflows.RejectTaskAsync(acme, owner, rejectStart.Task!.Id, "needs changes");
            rejected.Instance.Status.Should().Be("rejected");
            var rejectedRecord = await records.GetRecordAsync(acme, "timesheets", rejectTimesheetId);
            rejectedRecord!["approval_status"].Should().Be("rejected");
            (await workflows.ListRecordActionsAsync(acme, owner, "timesheets", rejectTimesheetId)).Should().BeEmpty();

            var archivedTimesheetId = await RuntimeRecordTestHelper.CreateDraftTimesheetAsync(acme, records, owner, $"{suffix}-archived");
            await records.ArchiveRecordAsync(acme, owner, "timesheets", archivedTimesheetId);
            var startArchived = () => workflows.StartActionAsync(acme, owner, "timesheets", archivedTimesheetId, "submit_for_approval");
            await startArchived.Should().ThrowAsync<RuntimeWorkflowRecordNotFoundException>();

            var unknownAction = () => workflows.StartActionAsync(acme, owner, "timesheets", acmeTimesheetId, "drop_table");
            await unknownAction.Should().ThrowAsync<RuntimeWorkflowNotFoundException>();

            var detail = await workflows.GetInstanceAsync(acme, owner, start.Instance.Id);
            detail!.Events.Should().Contain(@event => @event.EventType == "timesheet.submitted");
            detail.Events.Should().Contain(@event => @event.EventType == "timesheet.approved");
            var standardHoursCondition = detail.Events.Should().ContainSingle(@event =>
                @event.EventType == "workflow.condition.evaluated").Subject;
            standardHoursCondition.Metadata.GetProperty("result").GetBoolean().Should().BeFalse();
            standardHoursCondition.Metadata.GetProperty("operator").GetString().Should().Be("gt");
            standardHoursCondition.Metadata.TryGetProperty("left", out _).Should().BeFalse();
            standardHoursCondition.Metadata.TryGetProperty("right", out _).Should().BeFalse();

            var overtimeTimesheetId = await RuntimeRecordTestHelper.CreateDraftTimesheetAsync(acme, records, owner, $"{suffix}-overtime", hours: 10);
            var overtimeStart = await workflows.StartActionAsync(acme, owner, "timesheets", overtimeTimesheetId, "submit_for_approval");
            var overtimeAfterFirstApproval = await workflows.ApproveTaskAsync(acme, owner, overtimeStart.Task!.Id, "pm/fallback approved overtime");
            overtimeAfterFirstApproval.Instance.Status.Should().Be("waiting");
            (await records.GetRecordAsync(acme, "timesheets", overtimeTimesheetId))!["approval_status"].Should().Be("submitted");
            var overtimePendingTasks = await workflows.ListTasksAsync(acme, owner);
            var ownerOvertimeTask = overtimePendingTasks.Should().ContainSingle(task =>
                task.InstanceId == overtimeStart.Instance.Id &&
                task.StepId == "request_owner_overtime_approval" &&
                task.Status == "pending").Subject;
            var overtimeCondition = (await workflows.GetInstanceAsync(acme, owner, overtimeStart.Instance.Id))!
                .Events.Should().ContainSingle(@event =>
                    @event.EventType == "workflow.condition.evaluated").Subject;
            overtimeCondition.Metadata.GetProperty("result").GetBoolean().Should().BeTrue();
            overtimeCondition.Metadata.GetProperty("operator").GetString().Should().Be("gt");
            overtimeCondition.Metadata.TryGetProperty("left", out _).Should().BeFalse();
            overtimeCondition.Metadata.TryGetProperty("right", out _).Should().BeFalse();
            var overtimeApproved = await workflows.ApproveTaskAsync(acme, owner, ownerOvertimeTask.Id, "owner approved overtime");
            overtimeApproved.Instance.Status.Should().Be("completed");
            (await records.GetRecordAsync(acme, "timesheets", overtimeTimesheetId))!["approval_status"].Should().Be("approved");

            var auditEvents = await records.ListAuditEventsAsync(acme, 50);
            auditEvents.Should().Contain(@event => @event.EventType == "timesheet.submitted");
            auditEvents.Should().Contain(@event => @event.EventType == "timesheet.approved");
            auditEvents.Should().Contain(@event => @event.EventType == "timesheet.rejected");
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.started");
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.task.created");
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.task.approved");
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.task.rejected");
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.completed");
            auditEvents.Should().Contain(@event => @event.EventType == "workflow.rejected");
            (await records.GetRecordAsync(beta, "timesheets", betaTimesheetId))!["approval_status"].Should().Be("draft");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
        }
    }
}
