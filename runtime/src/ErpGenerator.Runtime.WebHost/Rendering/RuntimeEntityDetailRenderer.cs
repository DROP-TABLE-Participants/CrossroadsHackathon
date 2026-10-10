using static RuntimeHtml;

internal static class RuntimeEntityDetailRenderer
{
    public static string RenderRecordWorkflowPanel(
        RuntimeEntityDefinition entity,
        string recordId,
        IReadOnlyList<RuntimeRecordAction> actions,
        IReadOnlyList<RuntimeWorkflowInstanceDetail> workflowDetails)
    {
        if (actions.Count == 0 && workflowDetails.Count == 0)
        {
            return "";
        }

        var actionButtons = actions.Count == 0
            ? ""
            : $"""
              <div class="row-actions">
                {string.Join("", actions.Select(action => $"""<button class="button button-sm" type="button" data-runtime-action="{Html(action.Key)}">{Html(action.Label)}</button>"""))}
              </div>
              """;
        var instanceRows = workflowDetails.Count == 0
            ? $"""<tr><td colspan="4">{RuntimeUi.EmptyState("No workflow yet", "No workflow instance has started for this record. Approvals and stages appear here once a workflow runs.", "workflow")}</td></tr>"""
            : string.Join("", workflowDetails.Select(detail =>
            {
                var instance = detail.Instance;
                return $"""
              <tr>
                <td><a class="record-link" href="/workflow-instances/{instance.Id}">#{instance.Id}</a></td>
                <td class="cell-strong">{Html(instance.WorkflowName)}</td>
                <td>{RuntimeUi.StatusPill(instance.Status)}</td>
                <td class="nowrap cell-muted">{Html(FormatRecordValue(instance.UpdatedAt))}</td>
              </tr>
              """;
            }));
        var hasTimeline = workflowDetails.Any(detail => detail.Events.Count > 0);
        var timelineRows = !hasTimeline
            ? $"""<tr><td colspan="5">{RuntimeUi.EmptyState("No activity yet", "Workflow transitions and decisions will be recorded here as they happen.", "clock")}</td></tr>"""
            : string.Join("", workflowDetails.SelectMany(detail => detail.Events.Select(@event => $"""
              <tr>
                <td><a class="record-link" href="/workflow-instances/{detail.Instance.Id}">#{detail.Instance.Id}</a></td>
                <td class="cell-strong">{Html(RuntimeUi.Humanize(@event.EventType))}</td>
                <td>{Html(@event.Message)}</td>
                <td class="cell-muted">{Html(@event.TaskId is null ? "" : $"Task #{@event.TaskId}")}</td>
                <td class="nowrap cell-muted">{Html(FormatRecordValue(@event.CreatedAt))}</td>
              </tr>
              """)));

        return $$"""
          <section class="table-panel">
            <div class="panel-head">
              <div>
                <p class="eyebrow">Workflow</p>
                <h2>Approvals &amp; workflow</h2>
              </div>
              {{actionButtons}}
            </div>
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">Instance</th><th scope="col">Workflow</th><th scope="col">Status</th><th scope="col">Updated</th></tr></thead>
                <tbody>{{instanceRows}}</tbody>
              </table>
            </div>
            <div class="panel-head" style="border-top:1px solid var(--border)">
              <div><p class="eyebrow">Timeline</p><h2>Activity history</h2></div>
            </div>
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">Instance</th><th scope="col">Event</th><th scope="col">Message</th><th scope="col">Task</th><th scope="col">When</th></tr></thead>
                <tbody>{{timelineRows}}</tbody>
              </table>
            </div>
          </section>
          """;
    }
}
