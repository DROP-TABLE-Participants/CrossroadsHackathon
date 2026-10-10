using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleDashboardValidator
{
    public static void ValidateDashboard(ErpBundle bundle, List<string> errors)
    {
        var reportKeys = bundle.Reports.Select(report => report.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var metricKeysByReport = bundle.Reports.ToDictionary(
            report => report.Key,
            report => report.Metrics.Select(metric => metric.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var widgetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < bundle.Dashboard.Widgets.Count; index++)
        {
            var widget = bundle.Dashboard.Widgets[index];
            var path = $"dashboard.widgets[{index}]";
            RequireIdentifier(widget.Key, $"{path}.key", errors);
            RequireText(widget.Label, $"{path}.label", errors);
            RejectSqlLikeContent(widget.Key, $"{path}.key", errors);
            RejectSqlLikeContent(widget.Label, $"{path}.label", errors);
            RejectSqlLikeContent(widget.Type, $"{path}.type", errors);
            RejectSqlLikeContent(widget.Report, $"{path}.report", errors);
            RejectSqlLikeContent(widget.Metric, $"{path}.metric", errors);

            if (!widgetKeys.Add(widget.Key))
            {
                errors.Add($"Duplicate dashboard widget key '{widget.Key}'.");
            }

            var type = string.IsNullOrWhiteSpace(widget.Type) ? "metric" : widget.Type.Trim().ToLowerInvariant();
            if (!DashboardWidgetTypes.Contains(type))
            {
                errors.Add($"Dashboard widget '{widget.Key}' uses unsupported type '{widget.Type}'.");
            }

            if (string.IsNullOrWhiteSpace(widget.Report) || !reportKeys.Contains(widget.Report))
            {
                errors.Add($"Dashboard widget '{widget.Key}' references unknown report '{widget.Report}'.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(widget.Metric) &&
                metricKeysByReport.TryGetValue(widget.Report, out var metricKeys) &&
                !metricKeys.Contains(widget.Metric))
            {
                errors.Add($"Dashboard widget '{widget.Key}' references unknown metric '{widget.Metric}' on report '{widget.Report}'.");
            }
        }
    }
}
