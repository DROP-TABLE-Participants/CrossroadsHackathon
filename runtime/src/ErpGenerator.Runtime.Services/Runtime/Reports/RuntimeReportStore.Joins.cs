using Npgsql;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeReportStore
{
    private const int MaxRuntimeReportJoins = 3;
    private const int MaxRuntimeReportJoinDepth = 2;

    private async Task<RuntimeReportJoinPlan> BuildJoinPlanAsync(
        NpgsqlConnection connection,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        RuntimeReportDefinition report,
        RuntimeEntityDefinition sourceEntity,
        CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<string, RuntimeReportFieldContext>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = new(sourceEntity, BuildFieldLookup(sourceEntity.Fields), "t", 0, [sourceEntity.Id])
        };
        var joinParts = new List<string>();
        var accessParameters = new List<RuntimeRecordAccessParameter>();
        var joins = report.Joins ?? [];
        if (joins.Count > MaxRuntimeReportJoins)
        {
            throw new InvalidOperationException(
                $"Report '{report.Key}' exceeds the runtime relation join limit of {MaxRuntimeReportJoins}.");
        }

        for (var index = 0; index < joins.Count; index++)
        {
            var join = joins[index];
            EnsureIdentifier(join.Alias);
            EnsureIdentifier(join.FromAlias);
            EnsureIdentifier(join.RelationField);
            if (join.Alias.Equals("source", StringComparison.OrdinalIgnoreCase) || contexts.ContainsKey(join.Alias))
            {
                throw new InvalidOperationException($"Report '{report.Key}' contains unsafe or duplicate join alias '{join.Alias}'.");
            }

            if (!contexts.TryGetValue(join.FromAlias, out var from))
            {
                throw new InvalidOperationException(
                    $"Report '{report.Key}' join '{join.Alias}' references unknown or forward alias '{join.FromAlias}'.");
            }

            var depth = from.Depth + 1;
            if (join.Depth != depth || depth > MaxRuntimeReportJoinDepth)
            {
                throw new InvalidOperationException(
                    $"Report '{report.Key}' join '{join.Alias}' has invalid relation depth '{join.Depth}'.");
            }

            var relation = ResolveField(from.Fields, join.RelationField);
            if (relation is null ||
                !relation.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(relation.ReferencesEntity))
            {
                throw new InvalidOperationException(
                    $"Report '{report.Key}' join '{join.Alias}' does not traverse a declared relation field.");
            }

            var target = await LoadEntityByIdAsync(connection, join.TargetEntity, cancellationToken)
                         ?? throw new InvalidOperationException(
                             $"Report '{report.Key}' join '{join.Alias}' target entity '{join.TargetEntity}' was not found.");
            if (!relation.ReferencesEntity.Equals(target.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Report '{report.Key}' join '{join.Alias}' target does not match relation '{join.RelationField}'.");
            }

            if (!permissions.IsAllowed(snapshot, "read", target))
            {
                throw new RuntimeRecordPermissionException("read", target.Key);
            }

            if (from.AncestorEntities.Contains(target.Id, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Report '{report.Key}' join '{join.Alias}' creates a relation cycle back to entity '{target.Id}'.");
            }

            var joinType = join.Type.Trim().ToLowerInvariant();
            var keyword = joinType switch
            {
                "left" => "left join",
                "inner" => "inner join",
                _ => throw new InvalidOperationException(
                    $"Report '{report.Key}' join '{join.Alias}' uses unsupported type '{join.Type}'.")
            };
            var sqlAlias = $"j{index}";
            var targetContext = new RuntimeReportFieldContext(
                target,
                BuildFieldLookup(target.Fields),
                sqlAlias,
                depth,
                [.. from.AncestorEntities, target.Id]);
            contexts[join.Alias] = targetContext;
            var accessPredicate = await permissions.BuildRecordPredicateAsync(
                connection,
                transaction: null,
                snapshot,
                user,
                "read",
                target,
                sqlAlias,
                $"report_join_{index}",
                cancellationToken);
            accessParameters.AddRange(accessPredicate.Parameters);
            joinParts.Add(
                $"{keyword} {Quote(target.SchemaName)}.{Quote(target.TableName)} {sqlAlias} " +
                $"on {sqlAlias}.\"id\" = {from.SqlAlias}.{Quote(relation.ColumnName)} " +
                $"and {sqlAlias}.\"archived_at\" is null " +
                $"and ({accessPredicate.Sql})");
        }

        return new RuntimeReportJoinPlan(contexts, joinParts, accessParameters);
    }

    private static RuntimeReportResolvedField ResolveReportField(
        string fieldReference,
        IReadOnlyDictionary<string, RuntimeReportFieldContext> contexts)
    {
        if (string.IsNullOrWhiteSpace(fieldReference))
        {
            throw new InvalidOperationException("Report field reference is required.");
        }

        var parts = fieldReference.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"Unsafe report field reference '{fieldReference}'.");
        }

        foreach (var part in parts)
        {
            EnsureIdentifier(part);
        }

        var alias = parts.Length == 1 ? "source" : parts[0];
        var fieldKey = parts[^1];
        if (parts.Length == 1 && contexts.Values.Count(context => context.Fields.ContainsKey(fieldKey)) > 1)
        {
            throw new InvalidOperationException(
                $"Report field reference '{fieldReference}' is ambiguous and must be alias-qualified.");
        }

        if (!contexts.TryGetValue(alias, out var context))
        {
            throw new InvalidOperationException($"Report field reference '{fieldReference}' uses unknown alias '{alias}'.");
        }

        var field = ResolveField(context.Fields, fieldKey)
                    ?? throw new InvalidOperationException(
                        $"Report field reference '{fieldReference}' was not found on alias '{alias}'.");
        return new RuntimeReportResolvedField(context, field);
    }

    private static string QuoteQualifiedFieldOutput(string fieldReference)
    {
        var parts = fieldReference.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"Unsafe report field reference '{fieldReference}'.");
        }

        foreach (var part in parts)
        {
            EnsureIdentifier(part);
        }

        return $"\"{string.Join('.', parts)}\"";
    }

    private sealed record RuntimeReportJoinPlan(
        IReadOnlyDictionary<string, RuntimeReportFieldContext> Contexts,
        IReadOnlyList<string> JoinParts,
        IReadOnlyList<RuntimeRecordAccessParameter> AccessParameters);

    private sealed record RuntimeReportFieldContext(
        RuntimeEntityDefinition Entity,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> Fields,
        string SqlAlias,
        int Depth,
        IReadOnlyList<string> AncestorEntities);

    private sealed record RuntimeReportResolvedField(
        RuntimeReportFieldContext Context,
        RuntimeFieldDefinition Field);
}
