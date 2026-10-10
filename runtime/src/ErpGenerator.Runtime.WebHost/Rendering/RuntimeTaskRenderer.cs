using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeTaskRenderer
{
    public static string RenderTasksPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeWorkflowTask> tasks)
    {
        var rows = tasks.Count == 0
            ? $"""<tr><td colspan="7">{RuntimeUi.EmptyState("No tasks waiting", "New tasks appear when a workflow needs a decision.", "check")}</td></tr>"""
            : string.Join("", tasks.Select(task =>
            {
                var assigned = task.AssignedUserId == session.UserId
                    ? """<span class="badge">Assigned to you</span>"""
                    : task.AssignedUserId is not null
                        ? $"Runtime user #{task.AssignedUserId}"
                        : !string.IsNullOrWhiteSpace(task.AssignedRole)
                            ? $"Role: {Html(RuntimeUi.Humanize(task.AssignedRole))}"
                            : "Owner / admin review";
                return $"""
                  <tr>
                    <td><a class="record-link" href="/tasks/{task.Id}">#{task.Id}</a></td>
                    <td class="cell-strong">{Html(task.Label)}</td>
                    <td class="cell-muted">{Html(RuntimeUi.Humanize(task.WorkflowId))}</td>
                    <td><a class="record-link" href="/tasks/{task.Id}">{Html(RuntimeUi.Humanize(task.EntityId))} #{task.RecordId}</a></td>
                    <td>{assigned}</td>
                    <td>{RuntimeUi.StatusPill(task.Status)}</td>
                    <td class="col-actions"><div class="row-actions"><a class="button button-outline button-sm" href="/tasks/{task.Id}">Review</a></div></td>
                  </tr>
                  """;
            }));

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Tasks", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <div class="page-title-row"><h1>Tasks</h1></div>
              <p class="page-subtitle">Approvals and workflow tasks that need a decision.</p>
            </div>
          </section>
          <div class="result-bar"><span><strong>{{tasks.Count}}</strong> {{(tasks.Count == 1 ? "task" : "tasks")}}</span></div>
          <section class="table-panel">
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">Task</th><th scope="col">Name</th><th scope="col">Workflow</th><th scope="col">Record</th><th scope="col">Assigned</th><th scope="col">Status</th><th scope="col" class="col-actions">Review</th></tr></thead>
                <tbody>{{rows}}</tbody>
              </table>
            </div>
          </section>
          """;

        return ShellPage("Tasks", tenant, session, entities, body);
    }

    public static string RenderTaskDetailPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeWorkflowTaskRecordDetail detail)
    {
        var task = detail.Task;
        var isPending = task.Status.Equals("pending", StringComparison.OrdinalIgnoreCase);
        var evidenceRows = detail.Evidence.Count == 0
            ? $"""<tr><td colspan="2">{RuntimeUi.EmptyState("No evidence fields", "This task has no record fields attached for review.", "inbox")}</td></tr>"""
            : string.Join("", detail.Evidence.Select(field => $"""
              <tr>
                <th scope="row" style="width:38%">{Html(field.Label)}</th>
                <td class="cell-strong">{Html(FormatRecordValue(field.Value))}</td>
              </tr>
              """));

        var rejectButton = task.CanReject
            ? """<button class="button button-danger" type="button" data-task-decision="reject">Reject</button>"""
            : "";
        var decisions = isPending
            ? $"""
              <div class="row-actions">
                {rejectButton}
                <button class="button" type="button" data-task-decision="approve">Approve</button>
              </div>
              """
            : "";

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Tasks", "/tasks"), ($"#{task.Id}", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <p class="eyebrow">Workflow task #{{task.Id}}</p>
              <div class="page-title-row"><h1>{{Html(task.Label)}}</h1>{{RuntimeUi.StatusPill(task.Status)}}</div>
              <p class="page-subtitle">{{Html(RuntimeUi.Humanize(detail.EntityLabel))}} #{{detail.RecordId}} · {{Html(RuntimeUi.Humanize(task.WorkflowId))}}</p>
            </div>
            <div class="page-actions">{{decisions}}</div>
          </section>
          <section class="table-panel">
            <div class="panel-head"><div><h2>Decision evidence</h2></div></div>
            <div class="table-scroll">
              <table>
                <tbody>{{evidenceRows}}</tbody>
              </table>
            </div>
          </section>
          <script>
            (() => {
              for (const button of document.querySelectorAll('[data-task-decision]')) {
                button.addEventListener('click', async () => {
                  const decision = button.getAttribute('data-task-decision');
                  let reason = '';
                  if (decision === 'reject') {
                    const result = await window.erpgeneratorConfirm({
                      title: 'Reject this task?',
                      message: 'The record will not advance. Explain why so the requester understands.',
                      confirmLabel: 'Reject', tone: 'danger', requireReason: true
                    });
                    if (!result.confirmed) return;
                    reason = result.reason;
                  }
                  document.querySelectorAll('[data-task-decision]').forEach(b => b.disabled = true);
                  const original = button.innerHTML;
                  button.innerHTML = '<span class="spinner" aria-hidden="true"></span>Working…';
                  const response = await fetch(`/api/runtime/tasks/{{task.Id}}/${decision}`, {
                    method: 'POST',
                    headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ reason })
                  });
                  if (response.ok) {
                    window.erpgeneratorToast(decision === 'approve' ? 'Task approved.' : 'Task rejected.', { tone: 'success' });
                    setTimeout(() => window.location.assign('/tasks'), 600);
                    return;
                  }
                  document.querySelectorAll('[data-task-decision]').forEach(b => b.disabled = false);
                  button.innerHTML = original;
                  const payload = await response.json().catch(() => ({}));
                  window.erpgeneratorToast(payload.message || 'Unable to complete this task.', { tone: 'danger' });
                });
              }
            })();
          </script>
          """;

        return ShellPage($"Task #{task.Id}", tenant, session, entities, body);
    }
}
