using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleExceptionRuleValidator
{
    private static readonly IReadOnlySet<string> Comparators =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "eq",
            "neq",
            "gt",
            "gte",
            "lt",
            "lte"
        };

    private static readonly IReadOnlySet<string> Severities =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "info",
            "warning",
            "critical"
        };

    public static void Validate(ErpBundle bundle, BundleValidationContext context)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reports = bundle.Reports.ToDictionary(report => report.Key, StringComparer.OrdinalIgnoreCase);
        var workflows = bundle.Modules
            .SelectMany(module => module.Workflows)
            .Select(workflow => workflow.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in bundle.ExceptionRules)
        {
            var path = $"exception_rule[{rule.Id}]";
            RequireIdentifier(rule.Id, $"{path}.id", context.Errors);
            RequireText(rule.Label, $"{path}.label", context.Errors);
            RejectSqlLikeContent(rule.Label, $"{path}.label", context.Errors);
            if (!ids.Add(rule.Id))
            {
                context.Errors.Add($"Duplicate exception rule id '{rule.Id}'.");
            }

            if (!reports.TryGetValue(rule.Report, out var report))
            {
                context.Errors.Add($"{path}.report references unknown report '{rule.Report}'.");
            }
            else if (!report.Metrics.Any(metric =>
                         metric.Key.Equals(rule.Metric, StringComparison.OrdinalIgnoreCase)))
            {
                context.Errors.Add(
                    $"{path}.metric references unknown metric '{rule.Metric}' on report '{rule.Report}'.");
            }

            if (!Comparators.Contains(rule.Comparator))
            {
                context.Errors.Add($"{path}.comparator must be one of eq, neq, gt, gte, lt, or lte.");
            }

            if (!Severities.Contains(rule.Severity))
            {
                context.Errors.Add($"{path}.severity must be info, warning, or critical.");
            }

            RequireText(rule.Schedule, $"{path}.schedule", context.Errors);
            if (rule.Schedule.Length > 120)
            {
                context.Errors.Add($"{path}.schedule must be 120 characters or fewer.");
            }

            RequireIdentifier(rule.DeduplicationKey, $"{path}.deduplication_key", context.Errors);
            if (rule.RecipientRoles.Count == 0)
            {
                context.Errors.Add($"{path}.recipient_roles must contain at least one role.");
            }

            foreach (var role in rule.RecipientRoles)
            {
                if (!context.RoleIds.Contains(NormalizeRoleId(role)))
                {
                    context.Errors.Add($"{path}.recipient_roles references unknown role '{role}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(rule.ResolutionWorkflow) &&
                !workflows.Contains(rule.ResolutionWorkflow))
            {
                context.Errors.Add(
                    $"{path}.resolution_workflow references unknown workflow '{rule.ResolutionWorkflow}'.");
            }
        }
    }
}
