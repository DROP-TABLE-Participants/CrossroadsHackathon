using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed record RuntimeReportDefinition(
    string Key,
    string Label,
    string? Description,
    string SourceEntity,
    IReadOnlyList<RuntimeReportGroupBy> GroupBy,
    IReadOnlyList<RuntimeReportMetric> Metrics,
    IReadOnlyList<RuntimeReportFilter> Filters,
    IReadOnlyList<RuntimeReportJoin>? Joins = null);

public sealed record RuntimeReportJoin(
    string Alias,
    string FromAlias,
    string RelationField,
    string TargetEntity,
    string Type,
    int Depth);

public sealed record RuntimeReportGroupBy(
    string Field,
    string Label)
{
    public string? IdentityEntityId { get; init; }

    public string EvidenceValueKind { get; init; } = string.Empty;
}

public sealed record RuntimeReportMetric(
    string Key,
    string Label,
    string Op,
    string? Field,
    RuntimeReportFilter? Filter)
{
    public string EvidenceValueKind { get; init; } = string.Empty;
}

public sealed record RuntimeReportFilter(
    string Field,
    string Op,
    object? Value,
    IReadOnlyList<object?> Values);

public sealed record RuntimeReportQuery(
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    int Limit = 100);

public sealed record RuntimeReportResult(
    string Key,
    string Label,
    string? Description,
    IReadOnlyList<RuntimeReportColumn> Columns,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    DateTimeOffset GeneratedAt)
{
    public bool IsTruncated { get; init; }

    public int AppliedLimit { get; init; }
}

public sealed record RuntimeReportColumn(
    string Key,
    string Label,
    string Type)
{
    public string Kind { get; init; } = RuntimeReportColumnKinds.Value;

    public string ValueType { get; init; } = string.Empty;

    public string EvidenceValueKind { get; init; } = string.Empty;

    public string? IdentityEntityId { get; init; }
}

public static class RuntimeReportColumnKinds
{
    public const string Value = "value";
    public const string Identity = "identity";
    public const string Metric = "metric";
}

public sealed record RuntimeDashboardDefinition(
    IReadOnlyList<RuntimeDashboardWidget> Widgets);

public sealed record RuntimeDashboardWidget(
    string Key,
    string Label,
    string Type,
    string Report,
    string Metric);
