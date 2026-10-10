namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedErpSummaryViewModel(
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Workflows,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Reports,
    IReadOnlyList<string> Dashboards,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> OpenIssues);
