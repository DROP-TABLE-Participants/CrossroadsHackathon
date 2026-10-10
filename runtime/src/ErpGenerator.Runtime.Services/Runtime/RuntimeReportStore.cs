using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeReportStore : IRuntimeReportService
{
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly ITenantDatabaseConnectionFactory connectionFactory;
    private readonly RuntimePermissionService permissions;

    public RuntimeReportStore(ITenantDatabaseConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
        permissions = new RuntimePermissionService(connectionFactory);
    }

    public async Task<IReadOnlyList<RuntimeReportDefinition>> ListReportsAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var reports = new List<RuntimeReportDefinition>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select metadata
            from _runtime.report_definitions
            order by label
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reports.Add(ReadReport(reader.GetString(0)));
        }

        return reports;
    }

    public async Task<RuntimeReportDefinition?> GetReportAsync(
        ResolvedTenantEnvironment tenant,
        string reportKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select metadata
            from _runtime.report_definitions
            where id = @reportKey
            limit 1
            """;
        command.Parameters.AddWithValue("reportKey", reportKey.Trim());

        var metadata = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(metadata) ? null : ReadReport(metadata);
    }

    public async Task<RuntimeDashboardDefinition> GetDashboardAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select metadata
            from _runtime.dashboard_definitions
            where id = 'main'
            limit 1
            """;

        var metadata = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return new RuntimeDashboardDefinition([]);
        }

        var compiled = JsonSerializer.Deserialize<DashboardDefinition>(metadata, JsonOptions);
        return new RuntimeDashboardDefinition(
            compiled?.Widgets
                .Select(widget => new RuntimeDashboardWidget(widget.Key, widget.Label, widget.Type, widget.Report, widget.Metric))
                .ToArray() ?? []);
    }

    public async Task<RuntimeReportResult> ExecuteReportAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeReportDefinition report,
        RuntimeEntityDefinition entity,
        RuntimeReportQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!entity.Id.Equals(report.SourceEntity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Report '{report.Key}' source entity '{report.SourceEntity}' does not match runtime entity '{entity.Id}'.");
        }

        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var snapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        if (!permissions.IsAllowed(snapshot, "read", entity))
        {
            throw new RuntimeRecordPermissionException("read", entity.Key);
        }

        var sourcePredicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction: null,
            snapshot,
            user,
            "read",
            entity,
            "t",
            "report_source",
            cancellationToken);
        var joinPlan = await BuildJoinPlanAsync(
            connection,
            snapshot,
            user,
            report,
            entity,
            cancellationToken);
        EnsureReportFieldsAllowed(snapshot, report, joinPlan.Contexts, entity, query);
        var parameters = new List<NpgsqlParameter>();
        var selectParts = new List<string>();
        var groupParts = new List<string>();
        var columns = new List<RuntimeReportColumn>();

        foreach (var group in report.GroupBy)
        {
            var resolved = ResolveReportField(group.Field, joinPlan.Contexts);
            var expression = BuildFieldExpression(
                resolved.Field,
                resolved.Context.Fields,
                parameters,
                resolved.Context.SqlAlias);
            selectParts.Add($"{expression} as {QuoteQualifiedFieldOutput(group.Field)}");
            groupParts.Add(expression);
            columns.Add(new RuntimeReportColumn(group.Field, group.Label, resolved.Field.FieldType)
            {
                Kind = string.IsNullOrWhiteSpace(group.IdentityEntityId)
                    ? RuntimeReportColumnKinds.Value
                    : RuntimeReportColumnKinds.Identity,
                ValueType = resolved.Field.FieldType,
                EvidenceValueKind = group.EvidenceValueKind,
                IdentityEntityId = group.IdentityEntityId
            });
        }

        foreach (var metric in report.Metrics)
        {
            EnsureIdentifier(metric.Key);
            var expression = BuildMetricExpression(metric, joinPlan.Contexts, parameters);
            selectParts.Add($"{expression} as {Quote(metric.Key)}");
            columns.Add(new RuntimeReportColumn(metric.Key, metric.Label, "metric")
            {
                Kind = RuntimeReportColumnKinds.Metric,
                ValueType = ResolveMetricValueType(metric, joinPlan.Contexts),
                EvidenceValueKind = metric.EvidenceValueKind
            });
        }

        var whereParts = new List<string>
        {
            "t.\"archived_at\" is null",
            $"({sourcePredicate.Sql})"
        };
        foreach (var filter in report.Filters)
        {
            whereParts.Add(BuildFilterExpression(filter, joinPlan.Contexts, parameters));
        }

        AddDateWindowFilters(entity, joinPlan.Contexts["source"].Fields, query, whereParts, parameters);

        var limit = Math.Clamp(query.Limit <= 0 ? DefaultLimit : query.Limit, 1, MaxLimit);
        var fetchLimit = limit + 1;
        var groupBySql = groupParts.Count == 0 ? string.Empty : $"group by {string.Join(", ", groupParts)}";
        var orderBySql = groupParts.Count == 0 ? string.Empty : $"order by {string.Join(", ", report.GroupBy.Select(group => QuoteQualifiedFieldOutput(group.Field)))}";
        var commandText = $"""
            select {string.Join(", ", selectParts)}
            from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} t
            {string.Join(Environment.NewLine, joinPlan.JoinParts)}
            where {string.Join(" and ", whereParts)}
            {groupBySql}
            {orderBySql}
            limit @limit
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("limit", fetchLimit);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }
        sourcePredicate.AddParameters(command);
        foreach (var parameter in joinPlan.AccessParameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var rows = new List<Dictionary<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : NormalizeValue(reader.GetValue(index));
                }

                rows.Add(row);
            }
        }

        var truncated = rows.Count > limit;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        // Relation labels are not hydrated implicitly. Doing so would read a
        // target entity/display field that is absent from the report's typed
        // dependency and permission contract.
        return new RuntimeReportResult(report.Key, report.Label, report.Description, columns, rows, DateTimeOffset.UtcNow)
        {
            IsTruncated = truncated,
            AppliedLimit = limit
        };
    }
}
