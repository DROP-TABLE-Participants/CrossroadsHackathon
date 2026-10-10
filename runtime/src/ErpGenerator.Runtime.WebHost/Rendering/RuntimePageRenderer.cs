using ErpGenerator.Runtime.Shared.Tenants;

public static class RuntimePageRenderer
{
    public static string RenderDashboard(
        ResolvedTenantEnvironment tenant,
        RuntimeMetadata metadata,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyDictionary<string, long> counts,
        IReadOnlyList<RuntimeDashboardMetric> dashboardWidgets,
        IReadOnlyList<RuntimeDashboardActivityPoint> recordActivity,
        RuntimeSession session) =>
        RuntimeDashboardRenderer.RenderDashboard(tenant, metadata, entities, counts, dashboardWidgets, recordActivity, session);

    public static string RenderUnknownTenant() =>
        RuntimeErrorRenderer.RenderUnknownTenant();

    public static string RenderProvisioningShell(string tenantSlug) =>
        RuntimeErrorRenderer.RenderProvisioningShell(tenantSlug);

    public static string RenderNotFound(string title, string message) =>
        RuntimeErrorRenderer.RenderNotFound(title, message);

    public static string RenderLoginPage(ResolvedTenantEnvironment tenant, bool activated) =>
        RuntimeAuthRenderer.RenderLoginPage(tenant, activated);

    public static string RenderEntityList(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeRecordList list,
        RuntimeRecordQuery query,
        RuntimeEntityUiPermissions permissions) =>
        RuntimeEntityListRenderer.RenderEntityList(tenant, session, entities, list, query, permissions);

    public static string RenderEntityForm(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?>? record,
        IReadOnlyDictionary<string, IReadOnlyList<RuntimeLookupItem>> relationOptions,
        RuntimeEntityUiPermissions permissions,
        IReadOnlyList<RuntimeRecordAction> actions,
        IReadOnlyList<RuntimeWorkflowInstanceDetail> workflowDetails) =>
        RuntimeEntityFormRenderer.RenderEntityForm(tenant, session, entities, entity, record, relationOptions, permissions, actions, workflowDetails);

    public static string RenderReportsList(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeReportDefinition> reports) =>
        RuntimeReportRenderer.RenderReportsList(tenant, session, entities, reports);

    public static string RenderReportDetail(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeReportResult report,
        RuntimeReportQuery query) =>
        RuntimeReportRenderer.RenderReportDetail(tenant, session, entities, report, query);

    public static string RenderTasksPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeWorkflowTask> tasks) =>
        RuntimeTaskRenderer.RenderTasksPage(tenant, session, entities, tasks);

    public static string RenderTaskDetailPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeWorkflowTaskRecordDetail detail) =>
        RuntimeTaskRenderer.RenderTaskDetailPage(tenant, session, entities, detail);

    public static string RenderNotificationsPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeNotification> notifications,
        bool includeAll,
        bool canIncludeAll) =>
        RuntimeNotificationRenderer.RenderNotificationsPage(tenant, session, entities, notifications, includeAll, canIncludeAll);

    public static string RenderWorkflowInstancePage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        RuntimeWorkflowInstanceDetail detail) =>
        RuntimeWorkflowRenderer.RenderWorkflowInstancePage(tenant, session, entities, detail);

    public static string RenderUsersPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeManagedUser> users,
        IReadOnlyList<RuntimeRoleOption> roles,
        IReadOnlyList<RuntimeLookupItem> employees,
        string? activationUrl) =>
        RuntimeSettingsRenderer.RenderUsersPage(tenant, session, entities, users, roles, employees, activationUrl);

    public static string RenderInviteUserPage(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeRoleOption> roles,
        IReadOnlyList<RuntimeLookupItem> employees) =>
        RuntimeSettingsRenderer.RenderInviteUserPage(tenant, session, entities, roles, employees);

    public static string RenderDeploymentInfo(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        RuntimeMetadata metadata,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IReadOnlyList<RuntimeAuditEvent> events,
        IReadOnlyList<RuntimeBundleDeploymentHistoryItem> history) =>
        RuntimeSettingsRenderer.RenderDeploymentInfo(tenant, session, metadata, entities, events, history);

    public static string RenderActivationForm(string token, string? email = null, string? name = null) =>
        RuntimeAuthRenderer.RenderActivationForm(token, email, name);

    public static string RenderErrors(IReadOnlyList<RuntimeValidationError> errors) =>
        RuntimeErrorRenderer.RenderErrors(errors);

    public static string Page(string title, string body) =>
        RuntimeLayoutRenderer.Page(title, body);
}
