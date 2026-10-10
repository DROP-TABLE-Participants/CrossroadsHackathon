namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeReportStore
{
    private void EnsureReportFieldsAllowed(
        RuntimePermissionSnapshot snapshot,
        RuntimeReportDefinition report,
        IReadOnlyDictionary<string, RuntimeReportFieldContext> contexts,
        RuntimeEntityDefinition sourceEntity,
        RuntimeReportQuery query)
    {
        foreach (var join in report.Joins ?? [])
        {
            if (!contexts.TryGetValue(join.FromAlias, out var from))
            {
                throw new InvalidOperationException(
                    $"Report '{report.Key}' join '{join.Alias}' references unknown alias '{join.FromAlias}'.");
            }

            var relation = ResolveField(from.Fields, join.RelationField)
                           ?? throw new InvalidOperationException(
                               $"Report '{report.Key}' join '{join.Alias}' relation field '{join.RelationField}' was not found.");
            EnsureReportFieldAllowed(snapshot, from.Entity, relation);
        }

        var fieldReferences = report.GroupBy.Select(group => group.Field)
            .Concat(report.Metrics.Select(metric => metric.Field))
            .Concat(report.Filters.Select(filter => filter.Field))
            .Concat(report.Metrics
                .Where(metric => metric.Filter is not null)
                .Select(metric => metric.Filter!.Field))
            .Where(field => !string.IsNullOrWhiteSpace(field));
        foreach (var fieldReference in fieldReferences)
        {
            var resolved = ResolveReportField(fieldReference!, contexts);
            // A computed output is its own permission boundary. Its operand
            // fields are intentionally not expanded here.
            EnsureReportFieldAllowed(snapshot, resolved.Context.Entity, resolved.Field);
        }

        if (query.DateFrom is not null || query.DateTo is not null)
        {
            var dateWindowField = ResolveDateWindowField(sourceEntity);
            if (dateWindowField is not null)
            {
                EnsureReportFieldAllowed(snapshot, sourceEntity, dateWindowField);
            }
        }
    }

    private void EnsureReportFieldAllowed(
        RuntimePermissionSnapshot snapshot,
        RuntimeEntityDefinition entity,
        RuntimeFieldDefinition field)
    {
        if (!permissions.IsFieldAllowed(snapshot, "read", entity, field.Name))
        {
            throw new RuntimeRecordPermissionException("read", entity.Key);
        }
    }
}
