using System.Globalization;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeSettingsRenderer
{
    public static string RenderUsersPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeManagedUser> users,
        IReadOnlyList<RuntimeRoleOption> roles,
        IReadOnlyList<RuntimeLookupItem> employees,
        string? activationUrl)
    {
        var roleOptions = string.Join("", roles.Select(role => $"""<option value="{Html(role.Id)}">{Html(role.DisplayName)}</option>"""));
        var employeeOptions = string.Join("", new[] { new RuntimeLookupItem(0, "No employee link", null) }.Concat(employees).Select(employee =>
        {
            var value = employee.Id == 0 ? "" : Convert.ToString(employee.Id, CultureInfo.InvariantCulture);
            var label = employee.SecondaryLabel is null ? employee.Label : $"{employee.Label} — {employee.SecondaryLabel}";
            return $"""<option value="{Html(value)}">{Html(label)}</option>""";
        }));
        var rows = users.Count == 0
            ? $"""<tr><td colspan="6">{RuntimeUi.EmptyState("No users yet", "Invite your first teammate to give them access to this ERP.", "users")}</td></tr>"""
            : string.Join("", users.Select(user =>
            {
                var rolesText = string.Join(" ", user.Roles.Select(role => $"""<span class="badge">{Html(RuntimeUi.Humanize(role))}</span>"""));
                var toggleAction = user.Status.Equals("disabled", StringComparison.OrdinalIgnoreCase) ? "enable" : "disable";
                var toggleLabel = user.Status.Equals("disabled", StringComparison.OrdinalIgnoreCase) ? "Enable" : "Disable";
                var toggleClass = toggleAction == "enable" ? "button button-outline button-sm" : "button button-danger-soft button-sm";
                var toggleIcon = toggleAction == "enable" ? RuntimeUi.Icon("check", "") : RuntimeUi.Icon("x", "");
                var activationButton = user.Status.Equals("pending", StringComparison.OrdinalIgnoreCase)
                    ? """<button class="button button-outline button-sm" type="button" data-user-activation>Copy activation link</button>"""
                    : "";
                return $"""
                  <tr data-user-id="{user.Id}" data-user-email="{Html(user.Email)}" data-user-name="{Html(user.DisplayName)}" data-user-roles="{Html(string.Join(",", user.Roles))}">
                    <td class="cell-strong">{Html(user.DisplayName)}</td>
                    <td class="cell-muted">{Html(user.Email)}</td>
                    <td>{RuntimeUi.StatusPill(user.Status)}</td>
                    <td>{rolesText}</td>
                    <td class="cell-muted">{Html(user.LinkedEmployee?.Label ?? "Not linked")}</td>
                    <td>
                      <div class="user-manage">
                        <div class="user-manage-row">
                          <select data-role aria-label="Role to add">{roleOptions}</select>
                          <button class="button button-outline button-sm" type="button" data-user-role-add>Add role</button>
                        </div>
                        <div class="user-manage-row">
                          <select data-employee aria-label="Employee to link">{employeeOptions}</select>
                          <button class="button button-outline button-sm" type="button" data-user-link>Link</button>
                        </div>
                        <div class="user-manage-row">
                          <button class="{toggleClass}" type="button" data-user-toggle="{toggleAction}">{toggleIcon}{toggleLabel}</button>
                          {activationButton}
                        </div>
                        <div class="activation-inline" data-activation-output hidden></div>
                      </div>
                    </td>
                  </tr>
                  """;
            }));
        var activationPanel = string.IsNullOrWhiteSpace(activationUrl)
            ? ""
            : $"""
              <section class="notice tone-success">
                {RuntimeUi.Icon("check", "notice-icon")}
                <div class="notice-body" style="min-width:0">
                  <strong>Invite created</strong>
                  <span>Share this one-time activation link with the new user.</span>
                  <div style="display:flex;gap:8px;margin-top:8px;max-width:520px">
                    <input readonly value="{Html(activationUrl)}" id="activation-url" />
                    <button class="button button-outline button-sm" type="button" data-copy-target="activation-url">Copy</button>
                  </div>
                </div>
              </section>
              """;

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Users", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <div class="page-title-row"><h1>Users</h1></div>
              <p class="page-subtitle">Manage who can access this ERP and what they can do.</p>
            </div>
            <div class="page-actions"><a class="button" href="/settings/users/invite">{{RuntimeUi.Icon("plus", "")}}Invite user</a></div>
          </section>
          {{activationPanel}}
          <div class="result-bar"><span><strong>{{users.Count}}</strong> {{(users.Count == 1 ? "user" : "users")}}</span></div>
          <section class="table-panel">
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">Name</th><th scope="col">Email</th><th scope="col">Status</th><th scope="col">Roles</th><th scope="col">Linked employee</th><th scope="col">Manage</th></tr></thead>
                <tbody>{{rows}}</tbody>
              </table>
            </div>
          </section>
          <script>
            (() => {
              document.querySelectorAll('[data-copy-target]').forEach(button => button.addEventListener('click', async () => {
                const input = document.getElementById(button.getAttribute('data-copy-target'));
                try { await navigator.clipboard.writeText(input.value); window.erpgeneratorToast('Activation link copied.', { tone: 'success' }); }
                catch (e) { input.select(); window.erpgeneratorToast('Press Ctrl/Cmd+C to copy.', { tone: 'info' }); }
              }));
              for (const row of document.querySelectorAll('[data-user-id]')) {
                const userId = row.getAttribute('data-user-id');
                const reloadOn = async (response, message) => {
                  if (response.ok) { window.erpgeneratorToast(message, { tone: 'success' }); setTimeout(() => window.location.reload(), 500); }
                  else { const p = await response.json().catch(() => ({})); window.erpgeneratorToast(p.message || 'That change could not be applied.', { tone: 'danger' }); }
                };
                row.querySelector('[data-user-role-add]')?.addEventListener('click', async () => {
                  const role = row.querySelector('[data-role]').value;
                  reloadOn(await fetch(`/api/runtime/users/${userId}/roles`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ role }) }), 'Role added.');
                });
                row.querySelector('[data-user-link]')?.addEventListener('click', async () => {
                  const employeeRecordId = Number(row.querySelector('[data-employee]').value);
                  const response = employeeRecordId
                    ? await fetch(`/api/runtime/users/${userId}/link-employee`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ employeeRecordId }) })
                    : await fetch(`/api/runtime/users/${userId}/link-employee`, { method: 'DELETE' });
                  reloadOn(response, employeeRecordId ? 'Employee linked.' : 'Employee unlinked.');
                });
                row.querySelector('[data-user-toggle]')?.addEventListener('click', async (event) => {
                  const action = event.currentTarget.getAttribute('data-user-toggle');
                  reloadOn(await fetch(`/api/runtime/users/${userId}/${action}`, { method: 'POST' }), action === 'enable' ? 'User enabled.' : 'User disabled.');
                });
                row.querySelector('[data-user-activation]')?.addEventListener('click', async () => {
                  const roles = (row.getAttribute('data-user-roles') || '').split(',').filter(Boolean);
                  const response = await fetch('/api/runtime/users/invite', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: row.getAttribute('data-user-email'), name: row.getAttribute('data-user-name'), roles }) });
                  if (!response.ok) { window.erpgeneratorToast('Unable to create an activation link.', { tone: 'danger' }); return; }
                  const payload = await response.json();
                  const output = row.querySelector('[data-activation-output]');
                  if (payload.activationUrl && output) {
                    output.replaceChildren();
                    const input = document.createElement('input');
                    input.readOnly = true; input.value = payload.activationUrl;
                    output.append(input); output.hidden = false;
                    try { await navigator.clipboard.writeText(payload.activationUrl); window.erpgeneratorToast('Activation link copied.', { tone: 'success' }); } catch (e) {}
                  }
                });
              }
            })();
          </script>
          """;

        return ShellPage("Users", tenant, session, entities, body);
    }

    public static string RenderInviteUserPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeRoleOption> roles,
        IReadOnlyList<RuntimeLookupItem> employees)
    {
        var roleChecks = string.Join("", roles.Select(role =>
        {
            var isMember = role.Id.Equals("member", StringComparison.OrdinalIgnoreCase);
            return $"""<label class="inline-check"><input name="roles" type="checkbox" value="{Html(role.Id)}" {(isMember ? "checked" : "")} /> {Html(role.DisplayName)}</label>""";
        }));
        var employeeOptions = string.Join("", new[] { new RuntimeLookupItem(0, "No employee link", null) }.Concat(employees).Select(employee =>
        {
            var value = employee.Id == 0 ? "" : Convert.ToString(employee.Id, CultureInfo.InvariantCulture);
            var label = employee.SecondaryLabel is null ? employee.Label : $"{employee.Label} — {employee.SecondaryLabel}";
            return $"""<option value="{Html(value)}">{Html(label)}</option>""";
        }));

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Users", "/settings/users"), ("Invite", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <div class="page-title-row"><h1>Invite user</h1></div>
              <p class="page-subtitle">Create an activation link for a new ERP user.</p>
            </div>
            <div class="page-actions"><a class="button button-outline" href="/settings/users">{{RuntimeUi.Icon("arrow-left", "")}}Back to users</a></div>
          </section>
          <section class="form-panel">
            <form id="invite-user-form">
              <div id="form-errors" class="error-list" role="alert" hidden></div>
              <div class="form-section">
                <div class="form-section-head"><h2>Details</h2><span class="legend"><span class="req">*</span> Required field</span></div>
                <div class="form-grid">
                  <div class="field">
                    <label class="field-label" for="invite-email">Email <span class="req" title="Required">*</span></label>
                    <input id="invite-email" name="email" type="email" required />
                  </div>
                  <div class="field">
                    <label class="field-label" for="invite-name">Name <span class="req" title="Required">*</span></label>
                    <input id="invite-name" name="name" required />
                  </div>
                  <div class="field col-span-2">
                    <label class="field-label" for="invite-employee">Linked employee</label>
                    <select id="invite-employee" name="employeeRecordId">{{employeeOptions}}</select>
                    <p class="field-hint">Optionally connect this login to an employee record.</p>
                  </div>
                  <div class="field col-span-2">
                    <span class="field-label">Roles <span class="req" title="Required">*</span></span>
                    <div style="display:flex;flex-wrap:wrap;gap:14px">{{roleChecks}}</div>
                    <p class="field-hint">Select at least one role.</p>
                  </div>
                </div>
              </div>
              <div class="form-actions">
                <div class="spacer"></div>
                <a class="button button-outline" href="/settings/users">Cancel</a>
                <button class="button" type="submit">Send invite</button>
              </div>
              <div id="activation-link" class="success" hidden style="margin:0 20px 20px"></div>
            </form>
          </section>
          <script>
            (() => {
              document.querySelector('#invite-user-form').addEventListener('submit', async (event) => {
                event.preventDefault();
                const form = event.currentTarget;
                const roles = Array.from(form.querySelectorAll('input[name="roles"]:checked')).map(input => input.value);
                const errors = document.querySelector('#form-errors');
                const activation = document.querySelector('#activation-link');
                errors.hidden = true; activation.hidden = true;
                if (roles.length === 0) {
                  errors.replaceChildren();
                  const p = document.createElement('p'); p.textContent = 'Select at least one role.'; errors.append(p);
                  errors.hidden = false; return;
                }
                const employeeRecordId = form.employeeRecordId.value ? Number(form.employeeRecordId.value) : null;
                const submit = form.querySelector('button[type="submit"]');
                submit.disabled = true;
                const response = await fetch('/api/runtime/users/invite', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: form.email.value, name: form.name.value, roles, employeeRecordId }) });
                submit.disabled = false;
                if (!response.ok) {
                  const payload = await response.json().catch(() => ({}));
                  errors.replaceChildren();
                  const p = document.createElement('p'); p.textContent = payload.message || 'Unable to invite this user.'; errors.append(p);
                  errors.hidden = false; return;
                }
                const payload = await response.json();
                window.erpgeneratorToast('Invite created.', { tone: 'success' });
                activation.innerHTML = payload.activationUrl ? `Activation link: <input readonly value="${payload.activationUrl}" style="margin-top:6px" />` : 'User already exists. Roles were updated.';
                activation.hidden = false;
                form.reset();
              });
            })();
          </script>
          """;

        return ShellPage("Invite user", tenant, session, entities, body);
    }

    public static string RenderDeploymentInfo(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        RuntimeMetadata metadata,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeAuditEvent> events,
        IReadOnlyList<RuntimeBundleDeploymentHistoryItem> history)
    {
        var deploymentRows = history.Count == 0
            ? $"""<tr><td colspan="7">{RuntimeUi.EmptyState("No deployments yet", "ERP deployments appear here after preparation.", "package")}</td></tr>"""
            : string.Join("", history.Select(item =>
            {
                var operations = item.Operations.Count == 0
                    ? "No operations"
                    : string.Join(", ", item.Operations.Take(4).Select(operation => operation.Type)) +
                      (item.Operations.Count > 4 ? $" +{item.Operations.Count - 4} more" : "");
                var blocked = item.BlockedReasons.Count == 0
                    ? ""
                    : $"""<br /><span class="cell-muted">{Html(string.Join("; ", item.BlockedReasons))}</span>""";
                return $"""
                  <tr>
                    <td>#{item.Id}</td>
                    <td>{RuntimeUi.StatusPill(item.Status)}</td>
                    <td class="cell-muted">{Html(RuntimeUi.Humanize(item.Environment))}</td>
                    <td class="cell-strong">{Html(item.BundleVersion ?? "Unknown")}</td>
                    <td>{Html(RuntimeUi.Humanize(item.RiskLevel ?? "none"))}</td>
                    <td class="cell-muted">{Html(operations)}{blocked}</td>
                    <td class="nowrap cell-muted">{Html(FormatRecordValue(item.StartedAt))}</td>
                  </tr>
                  """;
            }));
        var auditRows = events.Count == 0
            ? $"""<tr><td colspan="4">{RuntimeUi.EmptyState("No runtime events", "Runtime activity will be recorded here.", "clock")}</td></tr>"""
            : string.Join("", events.Select(auditEvent => $"""
              <tr>
                <td>#{auditEvent.Id}</td>
                <td class="cell-strong">{Html(RuntimeUi.Humanize(auditEvent.EventType))}</td>
                <td>{Html(auditEvent.Message)}</td>
                <td class="nowrap cell-muted">{Html(FormatRecordValue(auditEvent.CreatedAt))}</td>
              </tr>
              """));
        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Settings", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <div class="page-title-row"><h1>Deployment</h1></div>
              <p class="page-subtitle">Environment, active bundle, and deployment history.</p>
            </div>
          </section>
          <section class="summary-card" aria-label="Deployment summary">
            <dl class="summary-grid">
              <div class="summary-item"><dt>Tenant</dt><dd>{{Html(RuntimeUi.HumanizeTenant(tenant.TenantSlug))}}</dd></div>
              <div class="summary-item"><dt>Environment</dt><dd>{{Html(tenant.Environment.ToString())}}</dd></div>
              <div class="summary-item"><dt>Host</dt><dd><span class="nowrap">{{Html(tenant.Host)}}</span></dd></div>
              <div class="summary-item"><dt>Bundle</dt><dd>{{Html(metadata.BundleVersion ?? "Unknown")}}</dd></div>
              <div class="summary-item"><dt>Entities</dt><dd>{{entities.Count}}</dd></div>
            </dl>
          </section>
          <section class="table-panel">
            <div class="panel-head"><div><h2>Deployment history</h2></div></div>
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">ID</th><th scope="col">Status</th><th scope="col">Environment</th><th scope="col">Version</th><th scope="col">Risk</th><th scope="col">Plan</th><th scope="col">Started</th></tr></thead>
                <tbody>{{deploymentRows}}</tbody>
              </table>
            </div>
          </section>
          <section class="table-panel">
            <div class="panel-head"><div><h2>Recent runtime events</h2></div></div>
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">ID</th><th scope="col">Event</th><th scope="col">Message</th><th scope="col">When</th></tr></thead>
                <tbody>{{auditRows}}</tbody>
              </table>
            </div>
          </section>
          """;

        return ShellPage("Deployment", tenant, session, entities, body);
    }
}
