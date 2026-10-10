using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeNotificationRenderer
{
    public static string RenderNotificationsPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeNotification> notifications,
        bool includeAll,
        bool canIncludeAll)
    {
        var unread = notifications.Count(notification => !notification.Status.Equals("read", StringComparison.OrdinalIgnoreCase));
        var rows = notifications.Count == 0
            ? $"""<tr><td colspan="6">{RuntimeUi.EmptyState("No notifications", includeAll ? "There are no notifications in this workspace yet." : "You have no notifications. Workflow updates and mentions will appear here.", "bell")}</td></tr>"""
            : string.Join("", notifications.Select(notification =>
            {
                var recipient = notification.RecipientUserId == session.UserId
                    ? "You"
                    : notification.RecipientUserId is not null
                        ? $"Runtime user #{notification.RecipientUserId}"
                        : !string.IsNullOrWhiteSpace(notification.RecipientRole)
                            ? $"Role: {Html(RuntimeUi.Humanize(notification.RecipientRole))}"
                            : "Unassigned";
                var target = string.IsNullOrWhiteSpace(notification.Link)
                    ? ""
                    : $"""<a class="button button-outline button-sm" href="{Html(notification.Link)}">Open</a>""";
                var readAction = notification.Status.Equals("read", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $"""<button class="button button-secondary button-sm" type="button" data-notification-read="{notification.Id}">Mark read</button>""";
                return $"""
                  <tr>
                    <td class="cell-strong">{Html(notification.Title)}</td>
                    <td>{Html(notification.Message)}</td>
                    <td class="cell-muted">{Html(recipient)}</td>
                    <td>{RuntimeUi.StatusPill(notification.Status)}</td>
                    <td class="nowrap cell-muted">{Html(FormatRecordValue(notification.CreatedAt))}</td>
                    <td class="col-actions"><div class="row-actions">{target}{readAction}</div></td>
                  </tr>
                  """;
            }));
        var includeAllLink = canIncludeAll
            ? $"""<a class="button button-outline" href="/notifications{(includeAll ? "" : "?includeAll=true")}">{(includeAll ? "My notifications" : "Show all")}</a>"""
            : "";
        var markAll = unread > 0
            ? """<button class="button" type="button" id="read-all-notifications">Mark all read</button>"""
            : "";

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Notifications", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <p class="eyebrow">Inbox</p>
              <div class="page-title-row"><h1>Notifications</h1></div>
              <p class="page-subtitle">Workflow updates, approvals, and mentions.</p>
            </div>
            <div class="page-actions">{{includeAllLink}}{{markAll}}</div>
          </section>
          <div class="result-bar"><span><strong>{{unread}}</strong> unread · {{notifications.Count}} total</span></div>
          <section class="table-panel">
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">Title</th><th scope="col">Message</th><th scope="col">Recipient</th><th scope="col">Status</th><th scope="col">Received</th><th scope="col" class="col-actions">Actions</th></tr></thead>
                <tbody>{{rows}}</tbody>
              </table>
            </div>
          </section>
          <script>
            (() => {
              for (const button of document.querySelectorAll('[data-notification-read]')) {
                button.addEventListener('click', async () => {
                  const id = button.getAttribute('data-notification-read');
                  button.disabled = true;
                  const response = await fetch(`/api/runtime/notifications/${id}/read`, { method: 'POST' });
                  if (response.ok) { window.erpgeneratorToast('Marked as read.', { tone: 'success' }); setTimeout(() => window.location.reload(), 500); }
                  else { button.disabled = false; window.erpgeneratorToast('Unable to mark this notification read.', { tone: 'danger' }); }
                });
              }
              document.querySelector('#read-all-notifications')?.addEventListener('click', async (event) => {
                event.currentTarget.disabled = true;
                const response = await fetch('/api/runtime/notifications/read-all', { method: 'POST' });
                if (response.ok) { window.erpgeneratorToast('All notifications marked read.', { tone: 'success' }); setTimeout(() => window.location.reload(), 500); }
                else { event.currentTarget.disabled = false; window.erpgeneratorToast('Unable to mark notifications read.', { tone: 'danger' }); }
              });
            })();
          </script>
          """;

        return ShellPage("Notifications", tenant, session, entities, body);
    }
}
