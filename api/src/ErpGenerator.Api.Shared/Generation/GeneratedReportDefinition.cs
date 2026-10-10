namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedReportDefinition(string Key, string Label, string EntityKey, IReadOnlyList<string> Columns);
