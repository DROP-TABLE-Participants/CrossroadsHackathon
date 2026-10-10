using System.Globalization;
using System.Net;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeDashboardRenderer
{
    public static string RenderDashboard(
        ResolvedTenantEnvironment tenant,
        RuntimeMetadata metadata,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyDictionary<string, long> counts,
        IReadOnlyList<RuntimeDashboardMetric> dashboardWidgets,
        IReadOnlyList<RuntimeDashboardActivityPoint> recordActivity,
        RuntimeSession session)
    {
        var company = tenant.TenantSlug == "demo-" + tenant.TenantStableId ? "Dandelion Chocolate" : RuntimeUi.HumanizeTenant(tenant.TenantSlug);
        var totalRecords = counts.Values.Sum();

        var reportMetrics = dashboardWidgets.Count == 0
            ? ""
            : $"""
              <div class="panel-head" style="margin:0 24px 12px 24px;border:0;padding:0">
                <h2>Key metrics</h2>
              </div>
              <section class="stat-grid">
                {string.Join("", dashboardWidgets.Select(widget => $"""
                  <a class="metric-card" href="/reports/{WebUtility.UrlEncode(widget.ReportKey)}">
                    <span class="metric-label">{RuntimeUi.Icon("reports", "")} {Html(widget.Label)}</span>
                    <div class="metric-value-row"><strong>{Html(FormatMetricValue(widget.Value))}</strong>{RuntimeUi.Icon("chevron-right", "chev")}</div>
                  </a>
                  """))}
              </section>
              """;

        var emptyBanner = totalRecords == 0 ? RenderDashboardEmptyState(entities) : "";

        // Records-by-type distribution — a real bar chart built from live counts.
        var ranked = entities
            .Select(entity => (entity, count: counts.GetValueOrDefault(entity.Key)))
            .OrderByDescending(x => x.count)
            .Take(8)
            .ToList();
        var maxCount = ranked.Count == 0 ? 1 : Math.Max(1, ranked.Max(x => x.count));
        var barRows = string.Join("", ranked.Select((x, index) =>
        {
            var width = (int)Math.Round(x.count * 100.0 / maxCount);
            return $"""
              <div class="bar-row">
                <div class="bar-label"><a href="/entities/{WebUtility.UrlEncode(x.entity.Key)}">{Html(x.entity.DisplayName)}</a></div>
                <div class="bar-track"><div class="bar-fill" style="--bar-width:{(x.count == 0 ? 0 : Math.Max(3, width))}%;--bar-delay:{index * 55}ms"></div></div>
                <div class="bar-value">{x.count.ToString("N0", CultureInfo.InvariantCulture)}</div>
              </div>
              """;
        }));
        var donut = BuildDistributionDonut(ranked, totalRecords);
        var barChart = totalRecords == 0
            ? ""
            : $"""
              <section class="analytics-grid" aria-label="ERP data charts">
                <article class="chart-panel">
                  <div class="panel-head"><div><h2>Share by record type</h2></div></div>
                  {donut}
                </article>
                <article class="chart-panel chart-panel-wide">
                  <div class="panel-head"><div><h2>Records by type</h2></div><span class="cell-muted chart-total">{totalRecords.ToString("N0", CultureInfo.InvariantCulture)} total</span></div>
                  <div class="bar-chart">{barRows}</div>
                </article>
              </section>
              """;

        var activityChart = BuildActivityChart(recordActivity);

        var assistantPanel = $"""
          <section class="erp-assistant" aria-labelledby="erp-assistant-title">
            <div class="erp-assistant-head">
              <span class="assistant-mark">{BrandAssets.MarkSvg}</span>
              <div>
                <h2 id="erp-assistant-title">Ask your ERP</h2>
              </div>
            </div>
            <form class="assistant-composer" data-erp-assistant-form>
              <label class="visually-hidden" for="erp-assistant-question">Question about your ERP</label>
              <textarea id="erp-assistant-question" rows="2" maxlength="2000" required data-erp-assistant-question placeholder="What needs my attention today?"></textarea>
              <div class="assistant-composer-foot">
                <div class="assistant-prompts" aria-label="Suggested questions">
                  <button class="assistant-prompt" type="button" data-assistant-prompt="Summarize what needs my attention today.">Needs attention</button>
                  <button class="assistant-prompt" type="button" data-assistant-prompt="What are the most important patterns in the current ERP data?">Key patterns</button>
                  <button class="assistant-prompt" type="button" data-assistant-prompt="Which records should I review first, and why?">Review priorities</button>
                </div>
                <button class="button assistant-submit" type="submit">Ask</button>
              </div>
            </form>
            <div class="assistant-answer" data-erp-assistant-answer hidden aria-live="polite">

              <p data-assistant-answer-text></p>
              <ul class="assistant-highlights" data-assistant-highlights hidden></ul>
              <div class="assistant-sources" data-assistant-sources hidden></div>
            </div>
            <p class="field-error assistant-error" data-erp-assistant-error hidden aria-live="polite"></p>
          </section>
          {AssistantScript()}
          """;

        var body = $$"""
          <section class="page-head">
            <div class="page-head-main">
              <div class="page-title-row"><h1>{{Html(company)}}</h1></div>
              <p class="page-subtitle">Operations overview</p>
            </div>
            <div class="page-actions">
              <a class="button button-outline" href="/settings/deployment">{{RuntimeUi.Icon("settings", "")}}Deployment info</a>
            </div>
          </section>
          {{emptyBanner}}
          {{reportMetrics}}
          {{assistantPanel}}
          {{barChart}}
          {{activityChart}}
          """;

        return ShellPage("Dashboard", tenant, session, entities, body);
    }

    static string BuildActivityChart(IReadOnlyList<RuntimeDashboardActivityPoint> activity)
    {
        if (activity.Count == 0)
        {
            return "";
        }

        const double left = 54;
        const double right = 946;
        const double top = 24;
        const double bottom = 188;
        var maxValue = Math.Max(1, activity.Max(point => point.Value));
        var points = activity.Select((point, index) =>
        {
            var x = activity.Count == 1 ? left : left + index * (right - left) / (activity.Count - 1);
            var y = bottom - point.Value * (bottom - top) / maxValue;
            return (point, x, y);
        }).ToList();
        var pointList = string.Join(" ", points.Select(item => $"{item.x.ToString("0.##", CultureInfo.InvariantCulture)},{item.y.ToString("0.##", CultureInfo.InvariantCulture)}"));
        var areaPoints = $"{left},{bottom} {pointList} {right},{bottom}";
        var grid = string.Join("", Enumerable.Range(0, 5).Select(index =>
        {
            var y = top + index * (bottom - top) / 4;
            var value = (long)Math.Round(maxValue * (4 - index) / 4.0);
            return $"""<g><line x1="{left}" y1="{y.ToString("0.##", CultureInfo.InvariantCulture)}" x2="{right}" y2="{y.ToString("0.##", CultureInfo.InvariantCulture)}"/><text x="44" y="{(y + 4).ToString("0.##", CultureInfo.InvariantCulture)}">{value}</text></g>""";
        }));
        var labels = string.Join("", points.Where((_, index) => index % 2 == 0 || index == points.Count - 1).Select(item =>
            $"""<text class="trend-label" x="{item.x.ToString("0.##", CultureInfo.InvariantCulture)}" y="218" text-anchor="middle">{Html(item.point.Day.ToString("MMM d", CultureInfo.InvariantCulture))}</text>"""));
        var markers = string.Join("", points.Where(item => item.point.Value > 0).Select(item =>
            $"""<circle cx="{item.x.ToString("0.##", CultureInfo.InvariantCulture)}" cy="{item.y.ToString("0.##", CultureInfo.InvariantCulture)}" r="4"><title>{item.point.Value} record{(item.point.Value == 1 ? "" : "s")} on {Html(item.point.Day.ToString("MMM d", CultureInfo.InvariantCulture))}</title></circle>"""));
        var total = activity.Sum(point => point.Value);
        var peak = activity.OrderByDescending(point => point.Value).First();

        return $"""
          <section class="trend-panel" aria-labelledby="record-activity-title">
            <div class="panel-head trend-head">
              <div><h2 id="record-activity-title">Record creation</h2><span class="muted chart-total">Last 14 days</span></div>
              <div class="trend-summary"><strong>{total.ToString("N0", CultureInfo.InvariantCulture)}</strong><span>new records</span></div>
            </div>
            <div class="trend-chart-wrap">
              <svg class="trend-chart" viewBox="0 0 1000 232" role="img" aria-labelledby="record-activity-title record-activity-desc">
                <desc id="record-activity-desc">Records visible to you created per day. Peak: {peak.Value} on {Html(peak.Day.ToString("MMM d", CultureInfo.InvariantCulture))}.</desc>
                <g class="trend-grid">{grid}</g>
                <polygon class="trend-area" points="{areaPoints}" />
                <polyline class="trend-line" points="{pointList}" pathLength="1" />
                <g class="trend-markers">{markers}</g>
                <g class="trend-labels">{labels}</g>
              </svg>
            </div>
          </section>
          """;
    }

    static string BuildDistributionDonut(
        IReadOnlyList<(RuntimeEntityDefinition entity, long count)> ranked,
        long totalRecords)
    {
        var colors = new[] { "var(--primary)", "#0d9488", "#b45309", "#0284c7" };
        var top = ranked.Where(item => item.count > 0).Take(colors.Length).ToList();
        var stops = new List<string>();
        var legend = new List<string>();
        double cursor = 0;
        for (var index = 0; index < top.Count; index++)
        {
            var item = top[index];
            var end = cursor + item.count * 100.0 / totalRecords;
            stops.Add($"{colors[index]} {cursor.ToString("0.##", CultureInfo.InvariantCulture)}% {end.ToString("0.##", CultureInfo.InvariantCulture)}%");
            legend.Add($"""<li><span style="background:{colors[index]}"></span><b>{Html(item.entity.DisplayName)}</b><small>{Math.Round(item.count * 100.0 / totalRecords):0}%</small></li>""");
            cursor = end;
        }

        if (cursor < 99.99)
        {
            stops.Add($"var(--border-strong) {cursor.ToString("0.##", CultureInfo.InvariantCulture)}% 100%");
            legend.Add($"""<li><span style="background:var(--border-strong)"></span><b>Other</b><small>{Math.Round(100 - cursor):0}%</small></li>""");
        }

        return $"""
          <div class="donut-wrap">
            <div class="donut-chart" style="--donut-fill:conic-gradient({string.Join(",", stops)})">
              <span><strong>{totalRecords.ToString("N0", CultureInfo.InvariantCulture)}</strong><small>records</small></span>
            </div>
            <ul class="donut-legend">{string.Join("", legend)}</ul>
          </div>
          """;
    }

    static string AssistantScript() =>
        """
        <script>
        (() => {
          const form = document.querySelector('[data-erp-assistant-form]');
          if (!form) return;
          const question = form.querySelector('[data-erp-assistant-question]');
          const submit = form.querySelector('.assistant-submit');
          const answer = document.querySelector('[data-erp-assistant-answer]');
          const answerText = answer.querySelector('[data-assistant-answer-text]');
          const highlights = answer.querySelector('[data-assistant-highlights]');
          const sources = answer.querySelector('[data-assistant-sources]');
          const error = document.querySelector('[data-erp-assistant-error]');
          for (const prompt of document.querySelectorAll('[data-assistant-prompt]')) {
            prompt.addEventListener('click', () => {
              question.value = prompt.getAttribute('data-assistant-prompt') || '';
              question.focus();
            });
          }
          form.addEventListener('submit', async (event) => {
            event.preventDefault();
            const value = question.value.trim();
            if (value.length < 3) return;
            const original = submit.innerHTML;
            submit.disabled = true;
            submit.innerHTML = '<span class="spinner" aria-hidden="true"></span>Thinking';
            error.hidden = true;
            answer.hidden = true;
            try {
              const response = await fetch('/api/runtime/assistant/query', {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ question: value })
              });
              const body = await response.json().catch(() => ({}));
              if (!response.ok) throw new Error(body.message || 'The assistant could not answer right now.');
              answerText.textContent = body.answer || '';
              highlights.replaceChildren();
              for (const item of body.highlights || []) {
                const li = document.createElement('li');
                li.textContent = item;
                highlights.append(li);
              }
              highlights.hidden = !highlights.children.length;
              sources.replaceChildren();
              if ((body.sources || []).length) {
                const label = document.createElement('strong');
                label.textContent = 'Based on';
                sources.append(label);
                for (const item of body.sources) {
                  const link = document.createElement('a');
                  link.textContent = item.label || 'ERP evidence';
                  link.href = item.url || '#';
                  sources.append(link);
                }
              }
              sources.hidden = !sources.children.length;
              answer.hidden = false;
              answer.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            } catch (exception) {
              error.textContent = exception instanceof Error ? exception.message : 'The assistant could not answer right now.';
              error.hidden = false;
            } finally {
              submit.disabled = false;
              submit.innerHTML = original;
            }
          });
        })();
        </script>
        """;

    /// <summary>
    /// Builds AI-style highlight lines purely from the live figures on the page —
    /// every statement is factual and derived from the data (no fabricated trends).
    /// </summary>
    static List<(string Tone, string Html)> BuildInsights(
        IReadOnlyList<RuntimeEntityDefinition> entities,
        List<(RuntimeEntityDefinition entity, long count)> ranked,
        long totalRecords,
        long pendingTasks,
        long unreadNotifications,
        long runningWorkflows)
    {
        var list = new List<(string, string)>();
        string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        if (totalRecords > 0)
        {
            list.Add(("info", $"You're managing <strong>{N(totalRecords)}</strong> records across <strong>{entities.Count}</strong> record type{(entities.Count == 1 ? "" : "s")}."));
        }

        var top = ranked.FirstOrDefault();
        if (top.entity is not null && top.count > 0 && totalRecords > 0)
        {
            var pct = (int)Math.Round(top.count * 100.0 / totalRecords);
            list.Add(("info", $"<strong>{Html(top.entity.DisplayName)}</strong> is your largest dataset — {N(top.count)} records ({pct}% of the total)."));
        }

        if (pendingTasks > 0)
        {
            list.Add(("warning", $"<strong>{N(pendingTasks)}</strong> approval{(pendingTasks == 1 ? " is" : "s are")} waiting on a decision — clearing {(pendingTasks == 1 ? "it" : "them")} keeps work moving."));
        }
        else if (totalRecords > 0)
        {
            list.Add(("success", "No approvals are waiting — your team is all caught up."));
        }

        if (unreadNotifications > 0)
        {
            list.Add(("info", $"<strong>{N(unreadNotifications)}</strong> unread notification{(unreadNotifications == 1 ? "" : "s")} may need your attention."));
        }

        if (runningWorkflows > 0)
        {
            list.Add(("info", $"<strong>{N(runningWorkflows)}</strong> workflow{(runningWorkflows == 1 ? " is" : "s are")} currently in progress."));
        }

        return list.Take(4).ToList();
    }

    static string MetricLink(string href, string icon, string label, long value, string description) =>
        $"""
        <a class="metric-card" href="{Html(href)}">
          <span class="metric-label">{RuntimeUi.Icon(icon, "")} {Html(label)}</span>
          <div class="metric-value-row"><strong>{value.ToString("N0", CultureInfo.InvariantCulture)}</strong>{RuntimeUi.Icon("chevron-right", "chev")}</div>
          <small>{Html(description)}</small>
        </a>
        """;

    static string RenderDashboardEmptyState(IReadOnlyList<RuntimeEntityDefinition> entities)
    {
        var preferred = entities.FirstOrDefault(entity =>
            entity.Key.Equals("customers", StringComparison.OrdinalIgnoreCase) ||
            entity.Key.Equals("projects", StringComparison.OrdinalIgnoreCase) ||
            entity.Key.Equals("orders", StringComparison.OrdinalIgnoreCase)) ?? entities.FirstOrDefault();
        var action = preferred is null
            ? ""
            : $"""<a class="button button-sm" href="/entities/{WebUtility.UrlEncode(preferred.Key)}/new">{RuntimeUi.Icon("plus", "")}Create first {Html(preferred.DisplayName)}</a>""";

        return $"""
          <section class="notice tone-info">
            {RuntimeUi.Icon("info", "notice-icon")}
            <div class="notice-body">
              <strong>No operating data yet</strong>
              <span>Create your first record to populate lists, reports, and dashboard metrics.</span>
            </div>
            <div class="notice-actions">{action}</div>
          </section>
          """;
    }
}

public sealed record RuntimeDashboardActivityPoint(DateOnly Day, long Value);
