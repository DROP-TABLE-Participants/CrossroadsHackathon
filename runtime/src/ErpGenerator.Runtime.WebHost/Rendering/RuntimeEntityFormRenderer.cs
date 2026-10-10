using System.Globalization;
using System.Net;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeEntityFormRenderer
{
    public static string RenderEntityForm(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?>? record,
        IReadOnlyDictionary<string, IReadOnlyList<RuntimeLookupItem>> relationOptions,
        RuntimeEntityUiPermissions permissions,
        IReadOnlyList<RuntimeRecordAction> actions,
        IReadOnlyList<RuntimeWorkflowInstanceDetail> workflowDetails)
    {
        var isNew = record is null;
        var recordId = record is null ? null : Convert.ToString(record["id"], CultureInfo.InvariantCulture);
        var isArchived = record?.GetValueOrDefault("archived_at") is not null;
        var pageTitle = isNew ? $"Create {entity.DisplayName}" : $"{entity.DisplayName} #{recordId}";
        var canSubmit = isNew ? permissions.CanCreate : permissions.CanUpdate && !isArchived;

        var editableFields = entity.Fields
            .Where(field => !isNew || field.Lifecycle is null)
            .ToArray();
        var hasRequired = editableFields.Any(f => f.Required && f.Computed is null);
        var fields = string.Join("", editableFields
            .Select(field => RenderFieldInput(
                field,
                entities,
                record,
                relationOptions,
                disabled: !canSubmit && !isNew || !isNew && field.Lifecycle?.RequiresNamedAction == true)));

        var submitLabel = isNew ? $"Create {entity.DisplayName}" : "Save changes";
        var method = isNew ? "POST" : "PATCH";
        var endpoint = isNew
            ? $"/api/runtime/entities/{WebUtility.UrlEncode(entity.Key)}/records"
            : $"/api/runtime/entities/{WebUtility.UrlEncode(entity.Key)}/records/{WebUtility.UrlEncode(recordId)}";

        // Destructive / restore controls sit apart from the primary Save action.
        var archiveButton = !isNew && permissions.CanArchive && !isArchived
            ? """<button class="button button-danger-soft" type="button" id="archive-record">Archive record</button>"""
            : "";
        var restoreButton = !isNew && isArchived && permissions.CanRestore
            ? """<button class="button button-outline" type="button" id="restore-record">Restore record</button>"""
            : "";
        var saveAnother = isNew && permissions.CanCreate
            ? """<button class="button button-outline" type="button" id="save-another">Save &amp; create another</button>"""
            : "";
        var saveButton = canSubmit ? $"""<button class="button" type="submit" id="save-record">{Html(submitLabel)}</button>""" : "";
        var cancelButton = $"""<a class="button button-outline" href="/entities/{WebUtility.UrlEncode(entity.Key)}" data-cancel>Cancel</a>""";

        var actionsBar = $"""
          <div class="form-actions">
            {archiveButton}{restoreButton}
            <div class="spacer"></div>
            {cancelButton}{saveAnother}{saveButton}
          </div>
          """;

        var legend = hasRequired
            ? """<span class="legend"><span class="req">*</span> Required field</span>"""
            : "";

        var readOnlyNotice = !isNew && !permissions.CanUpdate && !isArchived
            ? Notice("info", "Read-only", "You have permission to view this record but not to edit it.")
            : "";
        var archivedBanner = isArchived
            ? Notice("warning", "Archived record",
                $"This record is hidden from active lists and reports. Reason: {ArchiveReason(record)}.")
            : "";

        var summary = isNew ? "" : RenderSummary(entity, record!, isArchived, recordId!);
        var workflowPanel = isNew ? "" : RuntimeEntityDetailRenderer.RenderRecordWorkflowPanel(entity, recordId!, actions, workflowDetails);
        var statusPill = RenderTitleStatus(entity, record);

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), (entity.DisplayName, $"/entities/{WebUtility.UrlEncode(entity.Key)}"), (isNew ? "New" : $"#{recordId}", null))}}
          <section class="page-head">
            <div class="page-head-main">
              <p class="eyebrow">{{Html(entity.DisplayName)}}</p>
              <div class="page-title-row">
                <h1>{{Html(pageTitle)}}</h1>
                {{(isNew ? "" : $"""<span class="record-id">#{Html(recordId)}</span>""")}}
                {{statusPill}}
              </div>
            </div>
            <div class="page-actions">
              <a class="button button-outline" href="/entities/{{WebUtility.UrlEncode(entity.Key)}}">{{RuntimeUi.Icon("arrow-left", "")}}Back to list</a>
            </div>
          </section>
          {{archivedBanner}}
          {{readOnlyNotice}}
          {{summary}}
          <section class="form-panel">
            <form id="record-form" novalidate>
              <div id="form-errors" class="error-list" role="alert" hidden></div>
              <div class="form-section">
                <div class="form-section-head">
                  <h2>Details</h2>
                  {{legend}}
                </div>
                <div class="form-grid">
                  {{fields}}
                </div>
              </div>
              {{actionsBar}}
            </form>
          </section>
          {{workflowPanel}}
          <script>
            (() => {
              const endpoint = {{JsonSerializer.Serialize(endpoint)}};
              const method = {{JsonSerializer.Serialize(method)}};
              const entityKey = {{JsonSerializer.Serialize(entity.Key)}};
              const recordId = {{JsonSerializer.Serialize(recordId ?? "")}};
              const fieldTypes = {{JsonSerializer.Serialize(entity.Fields.ToDictionary(field => field.Name, field => field.FieldType))}};
              const recordForm = document.querySelector('#record-form');
              let dirty = false;
              let bypassGuard = false;
              let createAnother = false;

              recordForm.addEventListener('input', () => { dirty = true; });
              recordForm.addEventListener('change', () => { dirty = true; });
              window.addEventListener('beforeunload', (event) => {
                if (dirty && !bypassGuard) { event.preventDefault(); event.returnValue = ''; }
              });
              document.querySelector('[data-cancel]')?.addEventListener('click', (event) => {
                if (dirty && !bypassGuard && !window.confirm('Discard unsaved changes?')) { event.preventDefault(); }
                else { bypassGuard = true; }
              });

              function setFormErrors(payload) {
                const errors = document.querySelector('#form-errors');
                errors.hidden = true;
                errors.replaceChildren();
                for (const fieldError of document.querySelectorAll('[data-field-error-for]')) { fieldError.hidden = true; fieldError.textContent = ''; }
                for (const control of document.querySelectorAll('#record-form [name]')) { control.removeAttribute('aria-invalid'); control.removeAttribute('aria-describedby'); }
                const items = payload.errors || [{ message: payload.message || 'Unable to save this record.' }];
                const title = document.createElement('p');
                title.className = 'error-list-title';
                title.textContent = items.length > 1 ? `Please fix ${items.length} problems before saving:` : 'Please fix the following before saving:';
                errors.append(title);
                errors.hidden = false;
                for (const error of items) {
                  const row = document.createElement('p');
                  row.textContent = `${error.field ? `${error.field}: ` : ''}${error.message}`;
                  errors.append(row);
                  if (error.field) {
                    const fieldError = document.querySelector(`[data-field-error-for="${CSS.escape(error.field)}"]`);
                    if (fieldError) { fieldError.textContent = error.message; fieldError.hidden = false; }
                    const control = document.querySelector(`#record-form [name="${CSS.escape(error.field)}"]`);
                    if (control) { control.setAttribute('aria-invalid', 'true'); control.setAttribute('aria-describedby', `field-error-${error.field}`); }
                  }
                }
                errors.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
              }

              async function submitForm() {
                const submitButtons = recordForm.querySelectorAll('button[type="submit"], #save-another');
                const body = {};
                for (const field of recordForm.querySelectorAll('[name]:not(:disabled)')) {
                  if (field.type === 'checkbox') body[field.name] = field.checked;
                  else if (field.value === '') body[field.name] = null;
                  else if (['number', 'decimal', 'money', 'integer', 'relation'].includes(fieldTypes[field.name])) body[field.name] = Number(field.value);
                  else body[field.name] = field.value;
                }
                submitButtons.forEach(b => b.disabled = true);
                const active = createAnother ? document.querySelector('#save-another') : document.querySelector('#save-record');
                const original = active ? active.innerHTML : '';
                if (active) active.innerHTML = '<span class="spinner" aria-hidden="true"></span>Saving…';
                let response;
                try {
                  response = await fetch(endpoint, { method, headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
                } catch (networkError) {
                  submitButtons.forEach(b => b.disabled = false);
                  if (active) active.innerHTML = original;
                  window.erpgeneratorToast('Network error — your changes were not saved. Please retry.', { tone: 'danger' });
                  return;
                }
                if (!response.ok) {
                  submitButtons.forEach(b => b.disabled = false);
                  if (active) active.innerHTML = original;
                  const payload = await response.json().catch(() => ({}));
                  setFormErrors(payload);
                  return;
                }
                const saved = await response.json().catch(() => null);
                dirty = false; bypassGuard = true;
                if (createAnother) { window.location.href = `/entities/${entityKey}/new`; return; }
                window.location.href = saved?.id ? `/entities/${entityKey}/${saved.id}` : `/entities/${entityKey}`;
              }

              recordForm.addEventListener('submit', (event) => { event.preventDefault(); createAnother = false; submitForm(); });
              document.querySelector('#save-another')?.addEventListener('click', () => { createAnother = true; submitForm(); });

              const archiveButton = document.querySelector('#archive-record');
              archiveButton?.addEventListener('click', async () => {
                const { confirmed, reason } = await window.erpgeneratorConfirm({
                  title: 'Archive this record?',
                  message: 'It will be hidden from active lists and reports. You can restore it later.',
                  confirmLabel: 'Archive', tone: 'danger', requireReason: true
                });
                if (!confirmed) return;
                bypassGuard = true;
                const response = await fetch(`${endpoint}?reason=${encodeURIComponent(reason)}`, { method: 'DELETE' });
                if (response.ok) { window.erpgeneratorToast('Record archived.', { tone: 'success' }); setTimeout(() => window.location.href = `/entities/${entityKey}`, 600); }
                else { bypassGuard = false; window.erpgeneratorToast('Unable to archive this record.', { tone: 'danger' }); }
              });

              const restoreButton = document.querySelector('#restore-record');
              restoreButton?.addEventListener('click', async () => {
                bypassGuard = true;
                const response = await fetch(`${endpoint}/restore`, { method: 'POST' });
                if (response.ok) { window.erpgeneratorToast('Record restored.', { tone: 'success' }); setTimeout(() => window.location.href = `/entities/${entityKey}/${recordId}`, 600); }
                else { bypassGuard = false; window.erpgeneratorToast('Unable to restore this record.', { tone: 'danger' }); }
              });

              for (const button of document.querySelectorAll('[data-runtime-action]')) {
                button.addEventListener('click', async () => {
                  const original = button.innerHTML;
                  button.disabled = true;
                  button.innerHTML = '<span class="spinner" aria-hidden="true"></span>Working…';
                  bypassGuard = true;
                  const action = button.getAttribute('data-runtime-action');
                  const response = await fetch(`/api/runtime/entities/${entityKey}/records/${recordId}/actions/${action}`, { method: 'POST' });
                  if (response.ok) {
                    const payload = await response.json().catch(() => null);
                    const url = payload?.created?.url || payload?.Created?.Url;
                    window.erpgeneratorToast('Action completed.', { tone: 'success' });
                    setTimeout(() => window.location.href = url || window.location.href, 500);
                    return;
                  }
                  bypassGuard = false;
                  button.disabled = false;
                  button.innerHTML = original;
                  const payload = await response.json().catch(() => ({}));
                  window.erpgeneratorToast(payload.message || 'Unable to run this action.', { tone: 'danger' });
                });
              }

              {{RelationAutocompleteScript}}
            })();
          </script>
          """;

        return ShellPage(pageTitle, tenant, session, entities, body);
    }

    static string RenderFieldInput(
        RuntimeFieldDefinition field,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyDictionary<string, object?>? record,
        IReadOnlyDictionary<string, IReadOnlyList<RuntimeLookupItem>> relationOptions,
        bool disabled)
    {
        var value = record is not null ? FormatInputValue(record.GetValueOrDefault(field.ColumnName)) : string.Empty;
        var type = field.FieldType.ToLowerInvariant();
        var required = field.Required ? "required" : "";
        var disabledAttribute = disabled ? "disabled" : "";
        var inputId = $"field-{field.Name}";
        var labelText = LabelMarkup(field);
        var wide = type is "text" ? " col-span-2" : "";

        if (field.Computed is not null)
        {
            var computed = $"""
              <label class="field-label" for="{Html(inputId)}">{labelText}</label>
              <input id="{Html(inputId)}" value="{Html(value)}" readonly disabled />
              <p class="field-hint">Calculated automatically by the ERP.</p>
              """;
            return WrapField(field.Name, computed, wide);
        }

        if (type == "boolean")
        {
            var boolean = $"""
              <label class="checkbox" for="{Html(inputId)}">
                <input id="{Html(inputId)}" name="{Html(field.Name)}" type="checkbox" {(value == "true" ? "checked" : "")} {disabledAttribute} />
                <span>{Html(field.DisplayName)}{(field.Required ? """ <span class="req" title="Required">*</span>""" : "")}</span>
              </label>
              """;
            return WrapField(field.Name, boolean, wide);
        }

        if (type == "relation")
        {
            return WrapField(field.Name, RenderRelationInput(field, entities, value, record, relationOptions.GetValueOrDefault(field.Name) ?? [], disabled, inputId, labelText), wide);
        }

        var control = type switch
        {
            "text" => $"""<textarea id="{Html(inputId)}" name="{Html(field.Name)}" rows="3" {required} {disabledAttribute}>{Html(value)}</textarea>""",
            "number" or "decimal" or "money" => $"""<input id="{Html(inputId)}" name="{Html(field.Name)}" type="number" inputmode="decimal" step="0.01" value="{Html(value)}" {required} {disabledAttribute} />""",
            "integer" => $"""<input id="{Html(inputId)}" name="{Html(field.Name)}" type="number" inputmode="numeric" step="1" value="{Html(value)}" {required} {disabledAttribute} />""",
            "date" => $"""<input id="{Html(inputId)}" name="{Html(field.Name)}" type="date" value="{Html(value)}" {required} {disabledAttribute} />""",
            "datetime" => $"""<input id="{Html(inputId)}" name="{Html(field.Name)}" type="datetime-local" value="{Html(value)}" {required} {disabledAttribute} />""",
            "enum" => $"""<select id="{Html(inputId)}" name="{Html(field.Name)}" {required} {disabledAttribute}>{RenderOptions(field.Options, value)}</select>""",
            _ => $"""<input id="{Html(inputId)}" name="{Html(field.Name)}" value="{Html(value)}" {required} {disabledAttribute} />"""
        };

        var lifecycleHint = field.Lifecycle?.RequiresNamedAction == true && record is not null
            ? """<p class="field-hint">Changed only through an authorized workflow action.</p>"""
            : "";
        var body = $"""
          <label class="field-label" for="{Html(inputId)}">{labelText}</label>
          {control}
          {lifecycleHint}
          """;
        return WrapField(field.Name, body, wide);
    }

    static string LabelMarkup(RuntimeFieldDefinition field) =>
        field.Required
            ? $"""{Html(field.DisplayName)} <span class="req" title="Required">*</span>"""
            : Html(field.DisplayName);

    static string RenderOptions(IReadOnlyList<string> options, string currentValue) =>
        string.Join("", new[] { "" }.Concat(options).Select(option =>
            $"""<option value="{Html(option)}" {(option == currentValue ? "selected" : "")}>{Html(string.IsNullOrWhiteSpace(option) ? "Select…" : RuntimeUi.Humanize(option))}</option>"""));

    static string RenderRelationInput(
        RuntimeFieldDefinition field,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        string currentValue,
        IReadOnlyDictionary<string, object?>? record,
        IReadOnlyList<RuntimeLookupItem> options,
        bool disabled,
        string inputId,
        string labelText)
    {
        var required = field.Required ? "required" : "";
        var disabledAttribute = disabled ? "disabled" : "";
        var searchId = $"{inputId}-search";
        var resultsId = $"relation-results-{field.Name}";
        var current = options.FirstOrDefault(option => Convert.ToString(option.Id, CultureInfo.InvariantCulture) == currentValue);
        var relationLabel = FormatRecordValue(record?.GetValueOrDefault($"{field.ColumnName}__label") ?? current?.Label);
        var secondary = FormatRecordValue(record?.GetValueOrDefault($"{field.ColumnName}__secondaryLabel") ?? current?.SecondaryLabel);
        var displayValue = string.IsNullOrWhiteSpace(relationLabel)
            ? ""
            : string.IsNullOrWhiteSpace(secondary) ? relationLabel : $"{relationLabel} — {secondary}";
        var targetEntity = entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, field.ReferencesEntity, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Key, field.ReferencesEntity, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, field.ReferencesEntity, StringComparison.OrdinalIgnoreCase));
        var lookupEntity = WebUtility.UrlEncode(targetEntity?.Key ?? field.ReferencesEntity ?? "");

        return $$"""
          <label class="field-label" for="{{Html(searchId)}}">{{labelText}}</label>
          <div class="combobox">
            <input id="{{Html(inputId)}}" name="{{Html(field.Name)}}" type="hidden" value="{{Html(currentValue)}}" {{required}} />
            <input
              id="{{Html(searchId)}}"
              type="search"
              value="{{Html(displayValue)}}"
              placeholder="Search {{Html(targetEntity?.DisplayName ?? "related records")}}"
              data-relation-autocomplete
              data-target-id="{{Html(inputId)}}"
              data-lookup-url="/api/runtime/entities/{{lookupEntity}}/lookup"
              aria-controls="{{Html(resultsId)}}"
              autocomplete="off"
              {{disabledAttribute}} />
            <div id="{{Html(resultsId)}}" class="autocomplete-results" hidden></div>
          </div>
          <p class="field-hint">Type to search {{Html(targetEntity?.DisplayName?.ToLowerInvariant() ?? "related records")}} by name.</p>
          """;
    }

    static string WrapField(string fieldName, string inner, string extraClass = "") =>
        $"""
        <div class="field{extraClass}" data-field="{Html(fieldName)}">
          {inner}
          <p class="field-error" id="field-error-{Html(fieldName)}" data-field-error-for="{Html(fieldName)}" hidden>{RuntimeUi.Icon("alert", "")}<span></span></p>
        </div>
        """;

    static string RenderTitleStatus(RuntimeEntityDefinition entity, IReadOnlyDictionary<string, object?>? record)
    {
        if (record is null)
        {
            return "";
        }

        var lifecycleField = entity.Fields.FirstOrDefault(f => f.Lifecycle is not null);
        if (lifecycleField is not null)
        {
            var value = FormatRecordValue(record.GetValueOrDefault(lifecycleField.ColumnName));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return RuntimeUi.StatusPill(value);
            }
        }

        return record.GetValueOrDefault("archived_at") is not null
            ? RuntimeUi.StatusPill("archived", "Archived")
            : "";
    }

    static string RenderSummary(RuntimeEntityDefinition entity, IReadOnlyDictionary<string, object?> record, bool isArchived, string recordId)
    {
        var items = new List<string>();

        var lifecycleField = entity.Fields.FirstOrDefault(f => f.Lifecycle is not null);
        if (lifecycleField is not null)
        {
            var value = FormatRecordValue(record.GetValueOrDefault(lifecycleField.ColumnName));
            items.Add(SummaryItem(lifecycleField.DisplayName, string.IsNullOrWhiteSpace(value) ? "<span class=\"cell-muted\">—</span>" : RuntimeUi.StatusPill(value)));
        }

        var primary = FormatRecordValue(record.GetValueOrDefault($"{entity.DisplayPrimaryField}__label") ?? record.GetValueOrDefault(entity.DisplayPrimaryField));
        if (!string.IsNullOrWhiteSpace(primary))
        {
            var primaryField = entity.Fields.FirstOrDefault(f => f.Name == entity.DisplayPrimaryField);
            items.Add(SummaryItem(primaryField?.DisplayName ?? "Name", Html(primary)));
        }

        if (!string.IsNullOrWhiteSpace(entity.DisplaySecondaryField))
        {
            var secondary = FormatRecordValue(record.GetValueOrDefault($"{entity.DisplaySecondaryField}__label") ?? record.GetValueOrDefault(entity.DisplaySecondaryField));
            if (!string.IsNullOrWhiteSpace(secondary))
            {
                var secondaryField = entity.Fields.FirstOrDefault(f => f.Name == entity.DisplaySecondaryField);
                items.Add(SummaryItem(secondaryField?.DisplayName ?? "Detail", Html(secondary)));
            }
        }

        AddTimestamp(items, "Created", record.GetValueOrDefault("created_at"));
        AddTimestamp(items, "Last updated", record.GetValueOrDefault("updated_at"));
        items.Add(SummaryItem("State", isArchived ? RuntimeUi.StatusPill("archived", "Archived") : RuntimeUi.StatusPill("active", "Active")));

        if (items.Count == 0)
        {
            return "";
        }

        return $"""
          <section class="summary-card" aria-label="Record summary">
            <dl class="summary-grid">{string.Join("", items)}</dl>
          </section>
          """;
    }

    static void AddTimestamp(List<string> items, string label, object? value)
    {
        var text = FormatRecordValue(value);
        if (!string.IsNullOrWhiteSpace(text))
        {
            items.Add(SummaryItem(label, $"""<span class="nowrap">{Html(text)}</span>"""));
        }
    }

    static string SummaryItem(string label, string valueHtml) =>
        $"""<div class="summary-item"><dt>{Html(label)}</dt><dd>{valueHtml}</dd></div>""";

    static string ArchiveReason(IReadOnlyDictionary<string, object?>? record)
    {
        var reason = FormatRecordValue(record?.GetValueOrDefault("archive_reason"));
        return Html(string.IsNullOrWhiteSpace(reason) ? "not recorded" : reason);
    }

    static string Notice(string tone, string title, string message) =>
        $"""
        <section class="notice tone-{tone}">
          {RuntimeUi.Icon(tone == "warning" ? "alert" : tone == "danger" ? "alert" : "info", "notice-icon")}
          <div class="notice-body"><strong>{Html(title)}</strong><span>{Html(message)}</span></div>
        </section>
        """;

    // The relation combobox: debounced remote search with full keyboard support.
    // Kept as a plain string so its braces don't collide with raw-string interpolation.
    private const string RelationAutocompleteScript =
        """
        for (const input of document.querySelectorAll('[data-relation-autocomplete]')) {
          const hidden = document.querySelector(`#${CSS.escape(input.getAttribute('data-target-id'))}`);
          const results = document.querySelector(`#${CSS.escape(input.getAttribute('aria-controls'))}`);
          let timer;
          let activeIndex = -1;
          input.setAttribute('role', 'combobox');
          input.setAttribute('aria-expanded', 'false');
          input.setAttribute('aria-autocomplete', 'list');
          results.setAttribute('role', 'listbox');
          const optionsOf = () => [...results.querySelectorAll('.autocomplete-option')];
          const close = () => { results.hidden = true; input.setAttribute('aria-expanded', 'false'); input.removeAttribute('aria-activedescendant'); activeIndex = -1; };
          const open = () => { results.hidden = false; input.setAttribute('aria-expanded', 'true'); };
          const setActive = (index) => {
            const options = optionsOf();
            if (!options.length) return;
            activeIndex = (index + options.length) % options.length;
            options.forEach((option, i) => {
              option.classList.toggle('is-active', i === activeIndex);
              option.setAttribute('aria-selected', i === activeIndex ? 'true' : 'false');
              if (i === activeIndex) { input.setAttribute('aria-activedescendant', option.id); option.scrollIntoView({ block: 'nearest' }); }
            });
          };
          async function search() {
            const url = input.getAttribute('data-lookup-url');
            const q = input.value.trim();
            results.replaceChildren();
            close();
            if (q.length < 1) return;
            const response = await fetch(`${url}?q=${encodeURIComponent(q)}&limit=20`);
            if (!response.ok) return;
            const items = await response.json();
            if (!items.length) {
              const empty = document.createElement('div');
              empty.className = 'autocomplete-empty';
              empty.textContent = 'No matching related records.';
              results.append(empty);
              open();
              return;
            }
            items.forEach((item, index) => {
              const option = document.createElement('button');
              option.type = 'button';
              option.className = 'autocomplete-option';
              option.id = `${results.id}-option-${index}`;
              option.setAttribute('role', 'option');
              option.setAttribute('aria-selected', 'false');
              option.textContent = item.secondaryLabel ? `${item.label} — ${item.secondaryLabel}` : item.label;
              option.addEventListener('click', () => { hidden.value = item.id; input.value = option.textContent; hidden.dispatchEvent(new Event('change', { bubbles: true })); close(); });
              results.append(option);
            });
            open();
          }
          input.addEventListener('input', () => { hidden.value = ''; clearTimeout(timer); timer = setTimeout(search, 180); });
          input.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') { if (!results.hidden) { event.preventDefault(); close(); } return; }
            if (results.hidden) { if (event.key === 'ArrowDown' && input.value.trim()) { event.preventDefault(); search(); } return; }
            if (event.key === 'ArrowDown') { event.preventDefault(); setActive(activeIndex + 1); }
            else if (event.key === 'ArrowUp') { event.preventDefault(); setActive(activeIndex - 1); }
            else if (event.key === 'Enter') { const options = optionsOf(); if (activeIndex >= 0 && options[activeIndex]) { event.preventDefault(); options[activeIndex].click(); } }
          });
          input.addEventListener('focus', () => { if (input.value.trim()) search(); });
          input.addEventListener('blur', () => setTimeout(close, 180));
        }
        """;
}
