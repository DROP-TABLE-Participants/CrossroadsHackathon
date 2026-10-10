using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeWorkflowTaskCapabilityTests
{
    [Fact]
    public void Explicit_approve_only_capability_blocks_rejection_even_with_a_legacy_reject_label()
    {
        var task = Task(canReject: false, rejectLabel: "Reject");

        task.CanReject.Should().BeFalse();
        RuntimeWorkflowStore.TaskSupportsDecision(task, "approved").Should().BeTrue();
        RuntimeWorkflowStore.TaskSupportsDecision(task, "rejected").Should().BeFalse();
    }

    [Fact]
    public void Task_list_requires_review_detail_before_emitting_any_decision()
    {
        var tenant = RuntimeTestTenant.Create("task-ui", "task-ui.erpgenerator.example", "unused");
        var session = new RuntimeSession(
            tenant.TenantStableId,
            tenant.Host,
            7,
            "approver@example.test",
            "approver",
            DateTimeOffset.UtcNow.AddHours(1));

        var approveOnly = RuntimeTaskRenderer.RenderTasksPage(
            tenant,
            session,
            [],
            [Task(canReject: false, rejectLabel: string.Empty)]);
        var rejectable = RuntimeTaskRenderer.RenderTasksPage(
            tenant,
            session,
            [],
            [Task(canReject: true, rejectLabel: "Reject")]);

        approveOnly.Should().Contain("href=\"/tasks/11\"").And.Contain(">Review</a>");
        approveOnly.Should().NotContain("data-task-decision=");
        approveOnly.Should().NotContain("href=\"/entities/");
        rejectable.Should().Contain("href=\"/tasks/11\"").And.NotContain("data-task-decision=");
        RuntimeWorkflowStore.TaskSupportsDecision(
                Task(canReject: true, rejectLabel: "Reject"),
                "rejected")
            .Should().BeTrue("an explicit rejection branch remains executable");
    }

    [Fact]
    public void Task_detail_renders_only_the_safe_review_projection()
    {
        var tenant = RuntimeTestTenant.Create("task-detail-ui", "task-detail-ui.erpgenerator.example", "unused");
        var session = new RuntimeSession(
            tenant.TenantStableId,
            tenant.Host,
            7,
            "customer@example.test",
            "customer_contact",
            DateTimeOffset.UtcNow.AddHours(1));
        var detail = new RuntimeWorkflowTaskRecordDetail(
            Task(canReject: false, rejectLabel: string.Empty),
            "work_order_completion",
            "Work order completion",
            23,
            [
                new RuntimeWorkflowTaskEvidenceField("work_order", "Work order", "relation", 41L),
                new RuntimeWorkflowTaskEvidenceField("inspection_result", "Inspection result", "text", "passed")
            ]);

        var html = RuntimeTaskRenderer.RenderTaskDetailPage(tenant, session, [], detail);
        var json = JsonSerializer.Serialize(detail);

        html.Should().Contain("Decision evidence");
        html.Should().Contain("Work order").And.Contain("passed");
        html.Should().Contain("/api/runtime/tasks/11/${decision}");
        json.Should().Contain("work_order").And.Contain("inspection_result");
        json.Should().NotContain("SchemaName").And.NotContain("TableName").And.NotContain("ColumnName");
        json.Should().NotContain("created_by").And.NotContain("archive_reason");
        json.Should().NotContain("Metadata").And.NotContain("approveLabel").And.NotContain("rejectLabel");
    }

    private static RuntimeWorkflowTask Task(bool canReject, string rejectLabel)
    {
        var now = DateTimeOffset.UtcNow;
        return new RuntimeWorkflowTask(
            11,
            5,
            "transfer_receipt",
            "destination_receipt",
            "Destination receipt",
            "warehouse_transfer",
            23,
            "pending",
            "warehouse_user",
            null,
            null,
            null,
            null,
            JsonSerializer.SerializeToElement(new
            {
                approveLabel = "Approve",
                rejectLabel,
                canReject
            }),
            now,
            now);
    }
}
