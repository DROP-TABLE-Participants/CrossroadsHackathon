namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedErpBundle(
    string Name,
    string Summary,
    IReadOnlyList<string> Modules,
    IReadOnlyList<GeneratedEntityDefinition> Entities,
    IReadOnlyList<GeneratedWorkflowDefinition> Workflows,
    IReadOnlyList<GeneratedReportDefinition> Reports,
    IReadOnlyList<GeneratedDashboardDefinition> Dashboards,
    IReadOnlyList<GeneratedPermissionDefinition> Permissions,
    IReadOnlyList<GeneratedAcceptanceTestDefinition> AcceptanceTests,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OpenIssues);
