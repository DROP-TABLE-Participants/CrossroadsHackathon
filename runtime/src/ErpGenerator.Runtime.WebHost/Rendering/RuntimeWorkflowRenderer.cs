using System.Net;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeWorkflowRenderer
{
    public static string RenderWorkflowInstancePage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeWorkflowInstanceDetail detail)
    {
        var instance = detail.Instance;
        var taskRows = detail.Tasks.Count == 0
            ? $"""<tr><td colspan="8">{RuntimeUi.EmptyState("No tasks", "This workflow has no tasks yet.", "tasks")}</td></tr>"""
            : string.Join("", detail.Tasks.Select(task =>
            {
                var assignee = task.AssignedUserId is not null
                    ? $"Runtime user #{task.AssignedUserId}"
                    : !string.IsNullOrWhiteSpace(task.AssignedRole)
                        ? $"Role: {RuntimeUi.Humanize(task.AssignedRole)}"
                        : "Unassigned";
                return $"""
              <tr>
                <td><a class="record-link" href="/tasks/{task.Id}">#{task.Id}</a></td>
                <td class="cell-strong">{Html(task.Label)}</td>
                <td class="cell-muted">{Html(RuntimeUi.Humanize(task.StepId))}</td>
                <td>{Html(assignee)}</td>
                <td>{RuntimeUi.StatusPill(task.Status)}</td>
                <td class="cell-muted">{Html(task.DecisionBy is null ? "" : $"User #{task.DecisionBy}")}</td>
                <td class="nowrap cell-muted">{Html(FormatRecordValue(task.DecisionAt))}</td>
                <td>{Html(task.Reason)}</td>
              </tr>
              """;
            }));
        var eventRows = detail.Events.Count == 0
            ? $"""<tr><td colspan="6">{RuntimeUi.EmptyState("No timeline events", "Workflow activity will appear here as the instance progresses.", "clock")}</td></tr>"""
            : string.Join("", detail.Events.Select(@event => $"""
              <tr>
                <td>#{@event.Id}</td>
                <td class="cell-strong">{Html(RuntimeUi.Humanize(@event.EventType))}</td>
                <td>{Html(@event.Message)}</td>
                <td class="cell-muted">{Html(@event.TaskId is null ? "" : $"Task #{@event.TaskId}")}</td>
                <td class="cell-muted">{Html(RenderEventMetadata(@event.Metadata))}</td>
                <td class="nowrap cell-muted">{Html(FormatRecordValue(@event.CreatedAt))}</td>
              </tr>
              """));

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Tasks", "/tasks"), ($"Workflow #{instance.Id}", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <p class="eyebrow">Workflow instance #{{instance.Id}}</p>
              <div class="page-title-row"><h1>{{Html(instance.WorkflowName)}}</h1>{{RuntimeUi.StatusPill(instance.Status)}}</div>
              <p class="page-subtitle">{{Html(RuntimeUi.Humanize(instance.EntityId))}} #{{instance.RecordId}} · step {{Html(RuntimeUi.Humanize(instance.CurrentStepId ?? "—"))}}</p>
            </div>
            <div class="page-actions">
              <a class="button button-outline" href="/entities/{{WebUtility.UrlEncode(instance.EntityKey)}}/{{instance.RecordId}}">{{RuntimeUi.Icon("external", "")}}Open record</a>
            </div>
          </section>
          <section class="summary-card" aria-label="Workflow summary">
            <dl class="summary-grid">
              <div class="summary-item"><dt>Status</dt><dd>{{RuntimeUi.StatusPill(instance.Status)}}</dd></div>
              <div class="summary-item"><dt>Current step</dt><dd>{{Html(RuntimeUi.Humanize(instance.CurrentStepId ?? "No pending step"))}}</dd></div>
              <div class="summary-item"><dt>Record</dt><dd>{{Html(RuntimeUi.Humanize(instance.EntityId))}} #{{instance.RecordId}}</dd></div>
              <div class="summary-item"><dt>Started</dt><dd><span class="nowrap">{{Html(FormatRecordValue(instance.CreatedAt))}}</span></dd></div>
              <div class="summary-item"><dt>Started by</dt><dd>{{Html(instance.StartedBy is null ? "System" : $"User #{instance.StartedBy}")}}</dd></div>
            </dl>
          </section>
          <section class="table-panel">
            <div class="panel-head"><div><p class="eyebrow">Workflow</p><h2>Tasks</h2></div></div>
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">Task</th><th scope="col">Name</th><th scope="col">Step</th><th scope="col">Assigned</th><th scope="col">Status</th><th scope="col">Decided by</th><th scope="col">Decided at</th><th scope="col">Reason</th></tr></thead>
                <tbody>{{taskRows}}</tbody>
              </table>
            </div>
          </section>
          <section class="table-panel">
            <div class="panel-head"><div><p class="eyebrow">Timeline</p><h2>Activity history</h2></div></div>
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">ID</th><th scope="col">Event</th><th scope="col">Message</th><th scope="col">Task</th><th scope="col">Detail</th><th scope="col">When</th></tr></thead>
                <tbody>{{eventRows}}</tbody>
              </table>
            </div>
          </section>
          """;

        return ShellPage($"Workflow #{instance.Id}", tenant, session, entities, body);
    }

    static string RenderEventMetadata(JsonElement metadata)
    {
        var fields = new[] { "stepId", "conditionId", "result", "assignedUserId", "assignedRole", "notificationId", "reason" };
        var values = new List<string>();
        foreach (var field in fields)
        {
            if (metadata.TryGetProperty(field, out var value) &&
                value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                values.Add($"{field}: {FormatJsonElement(value)}");
            }
        }

        return values.Count == 0 ? "" : string.Join(" / ", values);
    }

    static string FormatJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => "[" + string.Join(", ", element.EnumerateArray().Select(FormatJsonElement)) + "]",
        _ => element.GetRawText()
    };
}
