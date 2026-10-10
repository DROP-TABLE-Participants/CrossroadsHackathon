using System.Globalization;
using System.Net;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeEntityListRenderer
{
    public static string RenderEntityList(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeRecordList list,
        RuntimeRecordQuery query,
        RuntimeEntityUiPermissions permissions)
    {
        var entity = list.Entity;
        var displayFields = entity.Fields.Take(8).ToArray();
        var columnCount = displayFields.Length + 5; // select, id, updated, state, actions

        var headerCells = new List<string>
        {
            $$"""<th class="col-select sticky-col" style="left:0"><input type="checkbox" class="row-check" data-select-all aria-label="Select all rows on this page" {{(permissions.CanArchive ? "" : "disabled")}} /></th>""",
            SortHeader(entity.Key, query, "id", "ID", sticky: true)
        };
        headerCells.AddRange(displayFields.Select(field =>
            SortHeader(entity.Key, query, field.Computed is null ? field.Name : null, field.DisplayName, numeric: IsNumeric(field.FieldType))));
        headerCells.Add(SortHeader(entity.Key, query, "updated_at", "Updated"));
        headerCells.Add("""<th scope="col">State</th>""");
        headerCells.Add("""<th scope="col" class="col-actions">Actions</th>""");

        var rows = list.Records.Count == 0
            ? $"""<tr><td colspan="{columnCount}">{EmptyStateForList(entity, query, permissions)}</td></tr>"""
            : string.Join("", list.Records.Select(record =>
            {
                var id = Convert.ToString(record["id"], CultureInfo.InvariantCulture) ?? "";
                var cells = string.Join("", displayFields.Select(field => RenderListCell(field, record)));
                var isArchived = record.GetValueOrDefault("archived_at") is not null;
                var state = isArchived ? RuntimeUi.StatusPill("archived", "Archived") : RuntimeUi.StatusPill("active", "Active");
                return $"""
                  <tr data-record-id="{Html(id)}">
                    <td class="col-select sticky-col" style="left:0"><input type="checkbox" class="row-check" value="{Html(id)}" aria-label="Select record {Html(id)}" {(isArchived || !permissions.CanArchive ? "disabled" : "")} /></td>
                    <td class="sticky-col" style="left:36px"><a class="record-link" href="/entities/{WebUtility.UrlEncode(entity.Key)}/{WebUtility.UrlEncode(id)}">#{Html(id)}</a></td>
                    {cells}
                    <td class="nowrap cell-muted">{Html(FormatRecordValue(record.GetValueOrDefault("updated_at")))}</td>
                    <td>{state}</td>
                    <td class="col-actions">{RenderRowActions(entity.Key, id, isArchived, permissions)}</td>
                  </tr>
                  """;
            }));

        var totalPages = Math.Max(1, (int)Math.Ceiling(list.Total / (double)list.PageSize));
        var rangeFrom = list.Total == 0 ? 0 : (list.Page - 1) * list.PageSize + 1;
        var rangeTo = Math.Min(list.Total, (long)list.Page * list.PageSize);
        var previous = list.Page > 1
            ? $"""<a class="button button-outline button-sm" href="{BuildEntityListUrl(entity.Key, query with { Page = list.Page - 1 })}">Previous</a>"""
            : """<button class="button button-outline button-sm" disabled>Previous</button>""";
        var next = list.Page < totalPages
            ? $"""<a class="button button-outline button-sm" href="{BuildEntityListUrl(entity.Key, query with { Page = list.Page + 1 })}">Next</a>"""
            : """<button class="button button-outline button-sm" disabled>Next</button>""";

        var createButton = permissions.CanCreate
            ? $"""<a class="button" href="/entities/{WebUtility.UrlEncode(entity.Key)}/new">{RuntimeUi.Icon("plus", "")}Create {Html(entity.DisplayName)}</a>"""
            : "";

        var stateToggle = permissions.CanRestore
            ? $"""
              <div class="segmented" role="group" aria-label="Record state">
                <a class="{(query.IncludeArchived ? "" : "active")}" href="{BuildEntityListUrl(entity.Key, query with { Page = 1, IncludeArchived = false })}">Active</a>
                <a class="{(query.IncludeArchived ? "active" : "")}" href="{BuildEntityListUrl(entity.Key, query with { Page = 1, IncludeArchived = true })}">With archived</a>
              </div>
              """
            : "";

        var sortDirection = string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var pageSizeOptions = string.Join("", new[] { 10, 25, 50, 100 }.Select(size =>
            $"""<option value="{size}" {(list.PageSize == size ? "selected" : "")}>{size}</option>"""));

        // Active-filter chips (removable), shown above the table.
        var chips = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            chips.Add($"""<span class="filter-chip"><span class="chip-key">Search:</span> {Html(query.Search)} <a href="{BuildEntityListUrl(entity.Key, query with { Page = 1, Search = null })}" aria-label="Remove search filter">✕</a></span>""");
        }

        if (query.IncludeArchived)
        {
            chips.Add($"""<span class="filter-chip"><span class="chip-key">Including</span> archived <a href="{BuildEntityListUrl(entity.Key, query with { Page = 1, IncludeArchived = false })}" aria-label="Stop including archived records">✕</a></span>""");
        }

        var filterSection = chips.Count == 0
            ? ""
            : $"""<span class="filter-chips">{string.Join("", chips)}</span><a class="clear-filters" href="/entities/{WebUtility.UrlEncode(entity.Key)}">Clear all</a>""";

        var bulkBar = permissions.CanArchive
            ? """
              <div class="bulk-bar" data-bulk-bar>
                <strong data-bulk-count>0 selected</strong>
                <div class="spacer"></div>
                <button class="button button-outline button-sm" type="button" data-bulk-clear>Clear</button>
                <button class="button button-danger button-sm" type="button" data-bulk-archive>Archive selected</button>
              </div>
              """
            : "";

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), (entity.DisplayName, null))}}
          <section class="page-head">
            <div class="page-head-main">
              <div class="page-title-row"><h1>{{Html(entity.DisplayName)}}</h1></div>
            </div>
            <div class="page-actions">{{createButton}}</div>
          </section>
          <form class="toolbar" method="get" role="search" data-filter-form>
            <div class="toolbar-search">
              {{RuntimeUi.Icon("search", "search-icon")}}
              <input name="q" type="search" placeholder="Search {{Html(entity.DisplayName.ToLowerInvariant())}}" value="{{Html(query.Search)}}" aria-label="Search {{Html(entity.DisplayName)}}" />
            </div>
            <label class="field-inline"><span>Sort</span>
              <select name="sortBy">{{RenderSortOptions(entity, query.SortBy)}}</select>
            </label>
            <label class="field-inline"><span>Direction</span>
              <select name="sortDirection">
                <option value="desc" {{(sortDirection == "desc" ? "selected" : "")}}>Newest first</option>
                <option value="asc" {{(sortDirection == "asc" ? "selected" : "")}}>Oldest first</option>
              </select>
            </label>
            <label class="field-inline"><span>Show</span>
              <select name="pageSize">{{pageSizeOptions}}</select>
            </label>
            <input type="hidden" name="includeArchived" value="{{(query.IncludeArchived ? "true" : "false")}}" />
            <noscript><button class="button button-outline button-sm" type="submit">Apply</button></noscript>
            <div class="spacer"></div>
            {{stateToggle}}
            <div class="segmented" role="group" aria-label="Row density">
              <button type="button" data-density="comfortable" title="Comfortable rows" aria-label="Comfortable rows">{{RuntimeUi.Icon("menu", "")}}</button>
              <button type="button" data-density="compact" title="Compact rows" aria-label="Compact rows">{{RuntimeUi.Icon("sort-desc", "")}}</button>
            </div>
          </form>
          <div class="result-bar">
            <span><strong>{{list.Total.ToString("N0", CultureInfo.InvariantCulture)}}</strong> {{Html(list.Total == 1 ? "record" : "records")}}</span>
            {{filterSection}}
          </div>
          <section class="table-panel">
            <div class="table-scroll">
              <table data-entity-list>
                <thead><tr>{{string.Join("", headerCells)}}</tr></thead>
                <tbody>{{rows}}</tbody>
              </table>
            </div>
          </section>
          {{bulkBar}}
          <footer class="pager">
            <span>Page {{list.Page}} of {{totalPages}}{{(list.Total == 0 ? "" : $" · showing {rangeFrom}–{rangeTo} of {list.Total.ToString("N0", CultureInfo.InvariantCulture)}")}}</span>
            <div class="pager-buttons">{{previous}}{{next}}</div>
          </footer>
          <script>
            (() => {
              const entityKey = {{System.Text.Json.JsonSerializer.Serialize(entity.Key)}};
              const table = document.querySelector('[data-entity-list]');

              // Auto-apply filters: submit on select change and debounced search typing.
              const filterForm = document.querySelector('[data-filter-form]');
              if (filterForm) {
                filterForm.querySelectorAll('select').forEach(sel => sel.addEventListener('change', () => filterForm.submit()));
                const search = filterForm.querySelector('input[name="q"]');
                if (search) {
                  let searchTimer;
                  search.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => filterForm.submit(), 500); });
                  search.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); clearTimeout(searchTimer); filterForm.submit(); } });
                  if (search.value) { const v = search.value; search.focus(); search.value = ''; search.value = v; }
                }
              }

              // Density preference (persisted).
              const DENSITY_KEY = 'erpgenerator-density';
              const applyDensity = (mode) => {
                table.classList.toggle('table-compact', mode === 'compact');
                document.querySelectorAll('[data-density]').forEach(b => b.classList.toggle('active', b.getAttribute('data-density') === mode));
              };
              let density = 'comfortable';
              try { density = localStorage.getItem(DENSITY_KEY) || 'comfortable'; } catch (e) {}
              applyDensity(density);
              document.querySelectorAll('[data-density]').forEach(b => b.addEventListener('click', () => {
                const mode = b.getAttribute('data-density');
                applyDensity(mode);
                try { localStorage.setItem(DENSITY_KEY, mode); } catch (e) {}
              }));

              // Row selection + bulk bar.
              const boxes = () => [...table.querySelectorAll('tbody .row-check:not(:disabled)')];
              const bulkBar = document.querySelector('[data-bulk-bar]');
              const selectAll = document.querySelector('[data-select-all]');
              const syncSelection = () => {
                const selected = boxes().filter(b => b.checked);
                boxes().forEach(b => b.closest('tr').toggleAttribute('data-selected', b.checked));
                if (bulkBar) {
                  bulkBar.setAttribute('data-visible', selected.length ? 'true' : 'false');
                  bulkBar.querySelector('[data-bulk-count]').textContent = `${selected.length} selected`;
                }
                if (selectAll) selectAll.checked = boxes().length > 0 && selected.length === boxes().length;
                return selected;
              };
              selectAll?.addEventListener('change', () => { boxes().forEach(b => b.checked = selectAll.checked); syncSelection(); });
              boxes().forEach(b => b.addEventListener('change', syncSelection));
              document.querySelector('[data-bulk-clear]')?.addEventListener('click', () => { boxes().forEach(b => b.checked = false); syncSelection(); });

              const archive = async (ids, reason) => {
                let ok = 0;
                for (const id of ids) {
                  const res = await fetch(`/api/runtime/entities/${entityKey}/records/${id}?reason=${encodeURIComponent(reason)}`, { method: 'DELETE' });
                  if (res.ok) ok++;
                }
                return ok;
              };

              document.querySelector('[data-bulk-archive]')?.addEventListener('click', async () => {
                const ids = syncSelection().map(b => b.value);
                if (!ids.length) return;
                const { confirmed, reason } = await window.erpgeneratorConfirm({
                  title: `Archive ${ids.length} ${ids.length === 1 ? 'record' : 'records'}?`,
                  message: 'They will be hidden from active lists and reports. You can restore them later.',
                  confirmLabel: `Archive ${ids.length}`, tone: 'danger', requireReason: true
                });
                if (!confirmed) return;
                const ok = await archive(ids, reason);
                if (ok === ids.length) { window.erpgeneratorToast(`${ok} ${ok === 1 ? 'record' : 'records'} archived.`, { tone: 'success', title: 'Done' }); setTimeout(() => window.location.reload(), 700); }
                else window.erpgeneratorToast(`Archived ${ok} of ${ids.length}. Some could not be archived.`, { tone: 'danger' });
              });

              // Per-row archive / restore (delegated).
              table.addEventListener('click', async (event) => {
                const archiveBtn = event.target.closest('[data-archive]');
                const restoreBtn = event.target.closest('[data-restore]');
                if (archiveBtn) {
                  const id = archiveBtn.getAttribute('data-archive');
                  const { confirmed, reason } = await window.erpgeneratorConfirm({
                    title: `Archive record #${id}?`,
                    message: 'It will be hidden from active lists and reports. You can restore it later.',
                    confirmLabel: 'Archive', tone: 'danger', requireReason: true
                  });
                  if (!confirmed) return;
                  const res = await fetch(`/api/runtime/entities/${entityKey}/records/${id}?reason=${encodeURIComponent(reason)}`, { method: 'DELETE' });
                  if (res.ok) { window.erpgeneratorToast(`Record #${id} archived.`, { tone: 'success' }); setTimeout(() => window.location.reload(), 700); }
                  else window.erpgeneratorToast('Unable to archive this record.', { tone: 'danger' });
                } else if (restoreBtn) {
                  const id = restoreBtn.getAttribute('data-restore');
                  const res = await fetch(`/api/runtime/entities/${entityKey}/records/${id}/restore`, { method: 'POST' });
                  if (res.ok) { window.erpgeneratorToast(`Record #${id} restored.`, { tone: 'success' }); setTimeout(() => window.location.reload(), 700); }
                  else window.erpgeneratorToast('Unable to restore this record.', { tone: 'danger' });
                }
              });
            })();
          </script>
          """;

        return ShellPage(entity.DisplayName, tenant, session, entities, body);
    }

    static bool IsNumeric(string fieldType) =>
        fieldType.ToLowerInvariant() is "number" or "decimal" or "money" or "integer";

    /// <summary>
    /// Formats a numeric value with locale grouping so comparable figures align.
    /// Money keeps two decimals; integers none; general decimals preserve their
    /// own precision (no silent rounding). Returns null when the value is not a
    /// parseable number so the caller can fall back to the raw display.
    /// </summary>
    static string? FormatNumeric(string type, object? value)
    {
        if (value is null)
        {
            return "";
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return type switch
        {
            "money" => number.ToString("N2", CultureInfo.InvariantCulture),
            "integer" => number.ToString("N0", CultureInfo.InvariantCulture),
            _ => number.ToString("#,##0.##########", CultureInfo.InvariantCulture)
        };
    }

    static string RenderListCell(RuntimeFieldDefinition field, IReadOnlyDictionary<string, object?> record)
    {
        var type = field.FieldType.ToLowerInvariant();
        if (type == "enum" && field.Lifecycle is not null)
        {
            var lifecycleValue = FormatRecordValue(record.GetValueOrDefault(field.ColumnName));
            return string.IsNullOrWhiteSpace(lifecycleValue) ? "<td></td>" : $"<td>{RuntimeUi.StatusPill(lifecycleValue)}</td>";
        }

        if (type == "enum")
        {
            var enumValue = FormatFieldValue(field, record);
            return string.IsNullOrWhiteSpace(enumValue) ? "<td></td>" : $"""<td><span class="badge">{Html(enumValue)}</span></td>""";
        }

        if (type == "boolean")
        {
            var truthy = string.Equals(FormatRecordValue(record.GetValueOrDefault(field.ColumnName)), "True", StringComparison.OrdinalIgnoreCase);
            return truthy ? """<td><span class="badge">Yes</span></td>""" : """<td><span class="cell-muted">No</span></td>""";
        }

        if (IsNumeric(type))
        {
            return $"""<td class="num">{Html(FormatNumeric(type, record.GetValueOrDefault(field.ColumnName)) ?? FormatFieldValue(field, record))}</td>""";
        }

        if (type is "date" or "datetime")
        {
            return $"""<td class="nowrap cell-muted">{Html(FormatFieldValue(field, record))}</td>""";
        }

        return $"<td class=\"{(type == "text" ? "cell-text" : "cell-label")}\">{Html(FormatFieldValue(field, record))}</td>";
    }

    static string RenderRowActions(string entityKey, string recordId, bool isArchived, RuntimeEntityUiPermissions permissions)
    {
        var open = $"""<a class="button button-outline button-sm" href="/entities/{WebUtility.UrlEncode(entityKey)}/{WebUtility.UrlEncode(recordId)}">Open</a>""";
        var menuItems = new List<string>();
        if (!isArchived && permissions.CanArchive)
        {
            menuItems.Add($"""<button class="danger" type="button" role="menuitem" data-archive="{Html(recordId)}">Archive</button>""");
        }

        if (isArchived && permissions.CanRestore)
        {
            menuItems.Add($"""<button type="button" role="menuitem" data-restore="{Html(recordId)}">Restore</button>""");
        }

        var overflow = menuItems.Count == 0
            ? ""
            : $"""
              <div class="menu">
                <button class="icon-button button-sm" type="button" data-menu-toggle aria-label="More actions for record {Html(recordId)}" aria-haspopup="menu">{RuntimeUi.Icon("more", "")}</button>
                <div class="menu-panel" role="menu">{string.Join("", menuItems)}</div>
              </div>
              """;

        return $"""<div class="row-actions">{open}{overflow}</div>""";
    }

    static string EmptyStateForList(RuntimeEntityDefinition entity, RuntimeRecordQuery query, RuntimeEntityUiPermissions permissions)
    {
        var hasFilters = !string.IsNullOrWhiteSpace(query.Search) || query.IncludeArchived;
        if (hasFilters)
        {
            return RuntimeUi.EmptyState(
                "No records match your filters",
                $"No {entity.DisplayName.ToLowerInvariant()} match the current search or state. Adjust or clear the filters to see more.",
                "search",
                $"""<a class="button button-outline button-sm" href="/entities/{WebUtility.UrlEncode(entity.Key)}">Clear all filters</a>""");
        }

        var action = permissions.CanCreate
            ? $"""<a class="button button-sm" href="/entities/{WebUtility.UrlEncode(entity.Key)}/new">{RuntimeUi.Icon("plus", "")}Create {Html(entity.DisplayName)}</a>"""
            : "";
        var reason = permissions.CanCreate
            ? $"Create the first {entity.DisplayName.ToLowerInvariant()} record to start populating lists, reports, and dashboards."
            : $"No {entity.DisplayName.ToLowerInvariant()} have been created yet. You do not have permission to create one — ask an administrator.";
        return RuntimeUi.EmptyState($"No {entity.DisplayName.ToLowerInvariant()} yet", reason, "inbox", action);
    }

    static string SortHeader(string entityKey, RuntimeRecordQuery query, string? sortBy, string label, bool numeric = false, bool sticky = false)
    {
        var thClass = (numeric ? "num" : "") + (sticky ? (numeric ? " sticky-col" : "sticky-col") : "");
        var classAttr = string.IsNullOrWhiteSpace(thClass) ? "" : $" class=\"{thClass.Trim()}\"";
        var styleAttr = sticky ? " style=\"left:36px\"" : "";
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return $"<th scope=\"col\"{classAttr}{styleAttr}>{Html(label)}</th>";
        }

        var current = string.Equals(query.SortBy, sortBy, StringComparison.OrdinalIgnoreCase) ||
                      (string.IsNullOrWhiteSpace(query.SortBy) && sortBy == "id");
        var ascending = string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        var nextDirection = current && ascending ? "desc" : "asc";
        var ariaSort = !current ? "" : ascending ? " aria-sort=\"ascending\"" : " aria-sort=\"descending\"";
        var icon = !current ? RuntimeUi.Icon("sort", "") : ascending ? RuntimeUi.Icon("sort-asc", "") : RuntimeUi.Icon("sort-desc", "");
        var url = BuildEntityListUrl(entityKey, query with { Page = 1, SortBy = sortBy, SortDirection = nextDirection });
        return $"""<th scope="col"{classAttr}{styleAttr}{ariaSort}><a class="sort-link" href="{url}">{Html(label)} {icon}</a></th>""";
    }

    static string RenderSortOptions(RuntimeEntityDefinition entity, string? currentSort)
    {
        var options = new List<(string Value, string Label)>
        {
            ("id", "ID"),
            ("created_at", "Created"),
            ("updated_at", "Updated")
        };

        options.AddRange(entity.Fields
            .Where(field => field.Computed is null)
            .Select(field => (field.Name, field.DisplayName)));

        return string.Join("", options
            .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(option =>
            {
                var selected = string.Equals(option.Value, currentSort, StringComparison.OrdinalIgnoreCase) ||
                               (string.IsNullOrWhiteSpace(currentSort) && option.Value == "id");
                return $"""<option value="{Html(option.Value)}" {(selected ? "selected" : "")}>{Html(option.Label)}</option>""";
            }));
    }

    static string BuildEntityListUrl(string entityKey, RuntimeRecordQuery query)
    {
        var parameters = new List<string>();
        if (query.Page > 1) parameters.Add($"page={query.Page}");
        if (query.PageSize != 25) parameters.Add($"pageSize={query.PageSize}");
        if (!string.IsNullOrWhiteSpace(query.SortBy)) parameters.Add($"sortBy={WebUtility.UrlEncode(query.SortBy)}");
        if (!string.IsNullOrWhiteSpace(query.SortDirection)) parameters.Add($"sortDirection={WebUtility.UrlEncode(query.SortDirection)}");
        if (!string.IsNullOrWhiteSpace(query.Search)) parameters.Add($"q={WebUtility.UrlEncode(query.Search)}");
        if (query.IncludeArchived) parameters.Add("includeArchived=true");

        var queryString = parameters.Count == 0 ? "" : "?" + string.Join("&", parameters);
        return $"/entities/{WebUtility.UrlEncode(entityKey)}{queryString}";
    }
}
