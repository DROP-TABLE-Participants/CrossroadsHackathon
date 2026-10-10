using System.Globalization;
using System.Net;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeReportRenderer
{
    public static string RenderReportsList(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeReportDefinition> reports)
    {
        var rows = reports.Count == 0
            ? $"""<tr><td colspan="4">{RuntimeUi.EmptyState("No reports available", "There are no reports for your role yet. Reports are generated from your ERP's data model and permissions.", "reports")}</td></tr>"""
            : string.Join("", reports.Select(report => $"""
              <tr>
                <td><a class="record-link" href="/reports/{WebUtility.UrlEncode(report.Key)}">{Html(report.Label)}</a></td>
                <td class="cell-muted">{Html(RuntimeUi.Humanize(report.SourceEntity))}</td>
                <td>{string.Join(" ", report.Metrics.Select(metric => $"""<span class="badge">{Html(metric.Label)}</span>"""))}</td>
                <td class="cell-muted">{Html(report.Description)}</td>
              </tr>
              """));

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Reports", null))}}
          <section class="page-head">
            <div class="page-head-main">

              <div class="page-title-row"><h1>Reports</h1></div>
            </div>
          </section>
          <div class="result-bar"><span><strong>{{reports.Count}}</strong> {{(reports.Count == 1 ? "report" : "reports")}}</span></div>
          <section class="table-panel">
            <div class="table-scroll">
              <table>
                <thead><tr><th scope="col">Report</th><th scope="col">Source</th><th scope="col">Metrics</th><th scope="col">Description</th></tr></thead>
                <tbody>{{rows}}</tbody>
              </table>
            </div>
          </section>
          """;

        return ShellPage("Reports", tenant, session, entities, body);
    }

    public static string RenderReportDetail(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeReportResult report,
        RuntimeReportQuery query)
    {
        var headers = string.Join("", report.Columns.Select((column, index) =>
            $"""<th scope="col"{(IsNumericColumn(column) ? " class=\"num\"" : "")}><button class="table-sort" type="button" data-sort-column="{index}">{Html(column.Label)} {RuntimeUi.Icon("sort", "")}</button></th>"""));
        var rows = report.Rows.Count == 0
            ? $"""<tr><td colspan="{Math.Max(1, report.Columns.Count)}">{RuntimeUi.EmptyState("No rows matched", "No report rows matched the current date window. Adjust the dates or add source records.", "search")}</td></tr>"""
            : string.Join("", report.Rows.Select(row => $"""
              <tr>{string.Join("", report.Columns.Select(column => $"""<td{(IsNumericColumn(column) ? " class=\"num\"" : "")}>{Html(FormatReportCell(row, column))}</td>"""))}</tr>
              """));
        var dateFrom = query.DateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        var dateTo = query.DateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        var summaryCards = RenderReportSummaryCards(report);
        var exportUrl = BuildReportCsvUrl(report.Key, query);
        var truncated = report.IsTruncated
            ? Notice("warning", "Results truncated", $"Showing the first {report.AppliedLimit} rows. Narrow the date window or export to CSV for the full data set.")
            : "";

        var body = $$"""
          {{RuntimeUi.Breadcrumbs(("Dashboard", "/"), ("Reports", "/reports"), (report.Label, null))}}
          <section class="page-head">
            <div class="page-head-main">
              <div class="page-title-row"><h1>{{Html(report.Label)}}</h1></div>
              {{(string.IsNullOrWhiteSpace(report.Description) ? "" : $"<p class=\"page-subtitle\">{Html(report.Description)}</p>")}}
              <p class="page-subtitle">Generated {{Html(FormatRecordValue(report.GeneratedAt))}}</p>
            </div>
            <div class="page-actions">
              <a class="button button-outline" href="/reports">{{RuntimeUi.Icon("arrow-left", "")}}All reports</a>
              <a class="button" href="{{exportUrl}}">{{RuntimeUi.Icon("download", "")}}Export CSV</a>
            </div>
          </section>
          {{summaryCards}}
          <form class="toolbar" method="get" data-filter-form>
            <label class="field-inline"><span>From</span> <input name="dateFrom" type="date" value="{{Html(dateFrom)}}" /></label>
            <label class="field-inline"><span>To</span> <input name="dateTo" type="date" value="{{Html(dateTo)}}" /></label>
            <label class="field-inline"><span>Limit</span> <input name="limit" type="number" min="1" max="500" value="{{query.Limit}}" style="width:90px" /></label>
            <noscript><button class="button button-outline button-sm" type="submit">Apply</button></noscript>
          </form>
          {{truncated}}
          <div class="result-bar"><span><strong>{{report.Rows.Count.ToString("N0", CultureInfo.InvariantCulture)}}</strong> {{(report.Rows.Count == 1 ? "row" : "rows")}}</span></div>
          <section class="table-panel">
            <div class="table-scroll">
              <table data-sortable-table>
                <thead><tr>{{headers}}</tr></thead>
                <tbody>{{rows}}</tbody>
              </table>
            </div>
          </section>
          <script>
            (() => {
              const filterForm = document.querySelector('[data-filter-form]');
              if (filterForm) filterForm.querySelectorAll('input').forEach(i => i.addEventListener('change', () => filterForm.submit()));
              for (const button of document.querySelectorAll('[data-sort-column]')) {
                button.addEventListener('click', () => {
                  const table = button.closest('table');
                  const tbody = table.querySelector('tbody');
                  const index = Number(button.getAttribute('data-sort-column'));
                  const direction = button.getAttribute('data-direction') === 'asc' ? 'desc' : 'asc';
                  button.setAttribute('data-direction', direction);
                  for (const header of table.querySelectorAll('th')) header.removeAttribute('aria-sort');
                  button.closest('th')?.setAttribute('aria-sort', direction === 'asc' ? 'ascending' : 'descending');
                  const rows = Array.from(tbody.querySelectorAll('tr')).filter(row => !row.querySelector('.empty-state'));
                  rows.sort((left, right) => {
                    const a = left.children[index]?.textContent?.trim() || '';
                    const b = right.children[index]?.textContent?.trim() || '';
                    const an = Number(a.replace(/[,\s]/g, ''));
                    const bn = Number(b.replace(/[,\s]/g, ''));
                    const result = Number.isFinite(an) && Number.isFinite(bn) ? an - bn : a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
                    return direction === 'asc' ? result : -result;
                  });
                  tbody.replaceChildren(...rows);
                });
              }
            })();
          </script>
          """;

        return ShellPage(report.Label, tenant, session, entities, body);
    }

    static bool IsNumericColumn(RuntimeReportColumn column) =>
        column.Type.Equals("metric", StringComparison.OrdinalIgnoreCase) ||
        column.ValueType.ToLowerInvariant() is "number" or "decimal" or "money" or "integer";

    static string Notice(string tone, string title, string message) =>
        $"""
        <section class="notice tone-{tone}">
          {RuntimeUi.Icon(tone == "warning" ? "alert" : "info", "notice-icon")}
          <div class="notice-body"><strong>{Html(title)}</strong><span>{Html(message)}</span></div>
        </section>
        """;

    static string BuildReportCsvUrl(string reportKey, RuntimeReportQuery query)
    {
        var parameters = new List<string>();
        if (query.DateFrom is not null) parameters.Add($"dateFrom={query.DateFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (query.DateTo is not null) parameters.Add($"dateTo={query.DateTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (query.Limit != 100) parameters.Add($"limit={query.Limit}");
        var queryString = parameters.Count == 0 ? "" : "?" + string.Join("&", parameters);
        return $"/api/runtime/reports/{WebUtility.UrlEncode(reportKey)}.csv{queryString}";
    }

    static string RenderReportSummaryCards(RuntimeReportResult report)
    {
        if (report.Rows.Count != 1 || report.Columns.Any(column => column.Type != "metric"))
        {
            return "";
        }

        var row = report.Rows[0];
        return $"""
          <section class="stat-grid">
            {string.Join("", report.Columns.Select(column => $"""
              <article class="metric-card">
                <span class="metric-label">{RuntimeUi.Icon("reports", "")} {Html(column.Label)}</span>
                <strong>{Html(FormatMetricValue(row.GetValueOrDefault(column.Key)))}</strong>
              </article>
              """))}
          </section>
          """;
    }

    static string FormatReportCell(IReadOnlyDictionary<string, object?> row, RuntimeReportColumn column)
    {
        var label = row.GetValueOrDefault($"{column.Key}__label");
        return FormatRecordValue(label ?? row.GetValueOrDefault(column.Key));
    }
}
