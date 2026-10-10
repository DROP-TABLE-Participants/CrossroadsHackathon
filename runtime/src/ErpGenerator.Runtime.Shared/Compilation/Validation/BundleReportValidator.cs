using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleReportValidator
{
    public static void ValidateReports(
        ErpBundle bundle,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var reportKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in bundle.Reports)
        {
            var path = $"report[{report.Key}]";
            RequireIdentifier(report.Key, $"{path}.key", errors);
            RequireText(report.Label, $"{path}.label", errors);
            RejectSqlLikeContent(report.Key, $"{path}.key", errors);
            RejectSqlLikeContent(report.Label, $"{path}.label", errors);
            RejectSqlLikeContent(report.Description, $"{path}.description", errors);
            RejectSqlLikeContent(report.Source, $"{path}.source", errors);

            if (!reportKeys.Add(report.Key))
            {
                errors.Add($"Duplicate report key '{report.Key}'.");
            }

            if (string.IsNullOrWhiteSpace(report.Source))
            {
                errors.Add($"{path}.source is required.");
                continue;
            }

            var sourceEntity = ResolveEntityId(report.Source, fieldsByEntity);
            if (sourceEntity is null)
            {
                errors.Add($"Report '{report.Key}' references unknown source '{report.Source}'.");
                continue;
            }

            var fieldContexts = ValidateJoins(report, sourceEntity, fieldsByEntity, path, errors);
            if (report.GroupBy.Count > 1)
            {
                errors.Add($"Report '{report.Key}' supports at most one group_by field in this pass.");
            }

            for (var index = 0; index < report.GroupBy.Count; index++)
            {
                var group = report.GroupBy[index];
                RejectSqlLikeContent(group.Field, $"{path}.group_by[{index}].field", errors);
                RejectSqlLikeContent(group.Label, $"{path}.group_by[{index}].label", errors);
                ResolveReportField(
                    report,
                    group.Field,
                    fieldContexts,
                    $"{path}.group_by[{index}].field",
                    "group_by",
                    errors);
            }

            if (report.Metrics.Count == 0)
            {
                errors.Add($"Report '{report.Key}' must declare at least one metric.");
            }

            var metricKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < report.Metrics.Count; index++)
            {
                ValidateReportMetric(report, report.Metrics[index], fieldContexts, $"{path}.metrics[{index}]", metricKeys, errors);
            }

            for (var index = 0; index < report.Filters.Count; index++)
            {
                ValidateReportFilter(report, report.Filters[index], fieldContexts, $"{path}.filters[{index}]", errors);
            }
        }
    }

    static void ValidateReportMetric(
        ErpReport report,
        ErpReportMetric metric,
        IReadOnlyDictionary<string, ReportFieldContext> fieldContexts,
        string path,
        HashSet<string> metricKeys,
        List<string> errors)
    {
        RequireIdentifier(metric.Key, $"{path}.key", errors);
        RequireText(metric.Label, $"{path}.label", errors);
        RejectSqlLikeContent(metric.Key, $"{path}.key", errors);
        RejectSqlLikeContent(metric.Label, $"{path}.label", errors);
        RejectSqlLikeContent(metric.Op, $"{path}.op", errors);
        RejectSqlLikeContent(metric.Field, $"{path}.field", errors);

        if (!metricKeys.Add(metric.Key))
        {
            errors.Add($"Report '{report.Key}' contains duplicate metric key '{metric.Key}'.");
        }

        var op = metric.Op.Trim().ToLowerInvariant();
        if (!ReportMetricOps.Contains(op))
        {
            errors.Add($"Report '{report.Key}' metric '{metric.Key}' uses unsupported op '{metric.Op}'.");
            return;
        }

        ErpField? field = null;
        if (!string.IsNullOrWhiteSpace(metric.Field))
        {
            field = ResolveReportField(report, metric.Field, fieldContexts, $"{path}.field", $"metric '{metric.Key}'", errors);
        }

        if (op != "count" && field is null)
        {
            errors.Add($"Report '{report.Key}' metric '{metric.Key}' op '{op}' must declare field.");
            return;
        }

        if (field is not null && (op is "sum" or "avg") && !ArithmeticFieldTypes.Contains(field.Type))
        {
            errors.Add($"Report '{report.Key}' metric '{metric.Key}' op '{op}' requires numeric field '{field.Name}'.");
        }

        if (field is not null && (op is "min" or "max") && !ArithmeticFieldTypes.Contains(field.Type) &&
            !field.Type.Equals("date", StringComparison.OrdinalIgnoreCase) &&
            !field.Type.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Report '{report.Key}' metric '{metric.Key}' op '{op}' requires numeric or date field '{field.Name}'.");
        }

        if (metric.Filter is not null)
        {
            ValidateReportFilter(report, metric.Filter, fieldContexts, $"{path}.filter", errors);
        }
    }

    static void ValidateReportFilter(
        ErpReport report,
        ErpReportFilter filter,
        IReadOnlyDictionary<string, ReportFieldContext> fieldContexts,
        string path,
        List<string> errors)
    {
        RejectSqlLikeContent(filter.Field, $"{path}.field", errors);
        RejectSqlLikeContent(filter.Op, $"{path}.op", errors);
        RejectSqlLikeValue(filter.Value, $"{path}.value", errors);
        RejectSqlLikeValue(filter.Values, $"{path}.values", errors);

        var field = ResolveReportField(report, filter.Field, fieldContexts, $"{path}.field", "filter", errors);
        if (field is null)
        {
            return;
        }

        var op = filter.Op.Trim().ToLowerInvariant();
        if (!ReportFilterOps.Contains(op))
        {
            errors.Add($"Report '{report.Key}' filter on '{filter.Field}' uses unsupported op '{filter.Op}'.");
            return;
        }

        if (op is "exists" or "not_exists")
        {
            return;
        }

        var values = filter.Values.Count > 0 ? filter.Values : TryGetArrayValue(filter.Value, out var arrayValues) ? arrayValues : [];
        if (op is "in" or "not_in")
        {
            if (values.Count == 0)
            {
                errors.Add($"Report '{report.Key}' filter op '{op}' requires values.");
                return;
            }

            foreach (var value in values)
            {
                if (!IsValueCompatible(field, value))
                {
                    errors.Add($"Report '{report.Key}' filter op '{op}' contains a value incompatible with field '{field.Name}'.");
                    break;
                }
            }

            return;
        }

        if (filter.Value is null)
        {
            errors.Add($"Report '{report.Key}' filter op '{op}' requires value.");
            return;
        }

        if (!IsValueCompatible(field, filter.Value))
        {
            errors.Add($"Report '{report.Key}' filter compares field '{field.Name}' to an incompatible value.");
        }

        if (op is "gt" or "gte" or "lt" or "lte" &&
            !ArithmeticFieldTypes.Contains(field.Type) &&
            !field.Type.Equals("date", StringComparison.OrdinalIgnoreCase) &&
            !field.Type.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Report '{report.Key}' filter op '{op}' requires numeric or date field '{field.Name}'.");
        }
    }

    private static IReadOnlyDictionary<string, ReportFieldContext> ValidateJoins(
        ErpReport report,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        string path,
        List<string> errors)
    {
        var contexts = new Dictionary<string, ReportFieldContext>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = new(sourceEntity, fieldsByEntity[sourceEntity], 0, [sourceEntity])
        };

        if (report.Joins.Count > MaxReportJoins)
        {
            errors.Add($"Report '{report.Key}' supports at most {MaxReportJoins} relation joins.");
        }

        for (var index = 0; index < report.Joins.Count; index++)
        {
            var join = report.Joins[index];
            var joinPath = $"{path}.joins[{index}]";
            RejectSqlLikeContent(join.Alias, $"{joinPath}.alias", errors);
            RejectSqlLikeContent(join.From, $"{joinPath}.from", errors);
            RejectSqlLikeContent(join.Relation, $"{joinPath}.relation", errors);
            RejectSqlLikeContent(join.Type, $"{joinPath}.type", errors);
            RequireIdentifier(join.Alias, $"{joinPath}.alias", errors);
            RequireIdentifier(join.From, $"{joinPath}.from", errors);
            RequireIdentifier(join.Relation, $"{joinPath}.relation", errors);

            if (join.Alias.Equals("source", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Report '{report.Key}' join alias 'source' is reserved.");
                continue;
            }

            if (contexts.ContainsKey(join.Alias))
            {
                errors.Add($"Report '{report.Key}' contains duplicate join alias '{join.Alias}'.");
                continue;
            }

            if (!contexts.TryGetValue(join.From, out var fromContext))
            {
                errors.Add($"Report '{report.Key}' join '{join.Alias}' references unknown or forward from alias '{join.From}'.");
                continue;
            }

            var depth = fromContext.Depth + 1;
            if (depth > MaxReportJoinDepth)
            {
                errors.Add($"Report '{report.Key}' join '{join.Alias}' exceeds maximum relation join depth {MaxReportJoinDepth}.");
                continue;
            }

            if (!fromContext.Fields.TryGetValue(join.Relation, out var relationField))
            {
                errors.Add($"Report '{report.Key}' join '{join.Alias}' references unknown relation field '{join.Relation}' on alias '{join.From}'.");
                continue;
            }

            if (!relationField.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(relationField.References))
            {
                errors.Add($"Report '{report.Key}' join '{join.Alias}' field '{join.Relation}' is not a declared relation.");
                continue;
            }

            var targetEntity = ResolveEntityId(relationField.References, fieldsByEntity);
            if (targetEntity is null)
            {
                errors.Add($"Report '{report.Key}' join '{join.Alias}' relation '{join.Relation}' references unknown entity '{relationField.References}'.");
                continue;
            }

            if (fromContext.AncestorEntities.Contains(targetEntity, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Report '{report.Key}' join '{join.Alias}' creates a relation cycle back to entity '{targetEntity}'.");
                continue;
            }

            var joinType = join.Type.Trim().ToLowerInvariant();
            if (!ReportJoinTypes.Contains(joinType))
            {
                errors.Add($"Report '{report.Key}' join '{join.Alias}' uses unsupported type '{join.Type}'.");
                continue;
            }

            contexts[join.Alias] = new(
                targetEntity,
                fieldsByEntity[targetEntity],
                depth,
                [.. fromContext.AncestorEntities, targetEntity]);
        }

        return contexts;
    }

    private static ErpField? ResolveReportField(
        ErpReport report,
        string fieldReference,
        IReadOnlyDictionary<string, ReportFieldContext> contexts,
        string path,
        string usage,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(fieldReference))
        {
            errors.Add($"{path} is required.");
            return null;
        }

        var parts = fieldReference.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{path} must be an unqualified source field or an alias-qualified field.");
            return null;
        }

        foreach (var part in parts)
        {
            RequireIdentifier(part, path, errors);
        }

        var alias = parts.Length == 1 ? "source" : parts[0];
        var fieldKey = parts[^1];
        if (parts.Length == 1 &&
            contexts.Values.Count(context => context.Fields.ContainsKey(fieldKey)) > 1)
        {
            errors.Add(
                $"Report '{report.Key}' {usage} field '{fieldKey}' is ambiguous across source and joined entities; qualify it with 'source' or a join alias.");
            return null;
        }

        if (!contexts.TryGetValue(alias, out var context))
        {
            errors.Add($"Report '{report.Key}' {usage} references unknown join alias '{alias}'.");
            return null;
        }

        if (!context.Fields.TryGetValue(fieldKey, out var field))
        {
            errors.Add($"Report '{report.Key}' {usage} references unknown field '{fieldKey}' on alias '{alias}'.");
            return null;
        }

        return field;
    }

    private sealed record ReportFieldContext(
        string EntityId,
        IReadOnlyDictionary<string, ErpField> Fields,
        int Depth,
        IReadOnlyList<string> AncestorEntities);
}
