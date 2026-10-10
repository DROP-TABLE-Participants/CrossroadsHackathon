using static RuntimePageRenderer;
using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;

public static class PageRoutes
{
    public static IEndpointRouteBuilder MapPageRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeDatabaseService runtimeDatabase,
            IRuntimeRecordService records,
            IRuntimeReportService reports,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeUserService users,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var tenant = await ResolveTenantAsync(httpContext, controlPlane, cancellationToken);
            if (tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                var metadata = await runtimeDatabase.ReadMetadataAsync(tenant, cancellationToken);
                var session = await ResolveRuntimeSessionAsync(httpContext, tenant, sessions);
                if (session is null)
                {
                    return Results.Redirect("/login");
                }

                var user = await users.GetActiveUserContextAsync(tenant, session.UserId, cancellationToken);
                if (user is null)
                {
                    return Results.Redirect("/login");
                }

                session = session with { Role = user.Role };
                var runtime = new RuntimeRouteContext(tenant, session, user, null);
                var pageError = await AuthorizePageAsync(
                    runtime,
                    records,
                    permissions,
                    "dashboard",
                    "*",
                    cancellationToken);
                if (pageError is not null)
                {
                    return pageError;
                }

                var entities = await records.ListEntitiesAsync(tenant, cancellationToken);
                var visibleEntities = await FilterReadableEntitiesAsync(tenant, session, entities, permissions, cancellationToken);
                var counts = await records.CountRecordsAsync(tenant, user, visibleEntities, cancellationToken);
                var dashboardWidgets = await BuildDashboardWidgetsAsync(tenant, user, reports, records, permissions, cancellationToken);
                var recordActivity = await BuildRecordActivityAsync(tenant, user, visibleEntities, records, cancellationToken);
                return Results.Content(RenderDashboard(tenant, metadata, visibleEntities, counts, dashboardWidgets, recordActivity, session), "text/html");
            }
            catch
            {
                return Results.Content(RenderProvisioningShell(tenant.TenantSlug), "text/html");
            }
        });

        app.MapGet("/entities/{entityKey}", async (
            string entityKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "read", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var pageError = await AuthorizePageAsync(
                    runtime,
                    records,
                    permissions,
                    "entity_list",
                    authorized.Entity!.Id,
                    cancellationToken);
                if (pageError is not null)
                {
                    return pageError;
                }

                var query = BuildRecordQuery(httpContext.Request);
                if (query.IncludeArchived &&
                    !await permissions.IsAllowedAsync(
                        runtime.Tenant,
                        runtime.User!,
                        "restore",
                        authorized.Entity!,
                        cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "includeArchived", entityKey, cancellationToken);
                    return Results.StatusCode(403);
                }

                var list = await records.ListRecordsAsync(
                    runtime.Tenant,
                    runtime.User!,
                    authorized.Entity!.Key,
                    query,
                    cancellationToken);
                list = await permissions.FilterReadableRecordListFieldsAsync(
                    runtime.Tenant,
                    runtime.User!,
                    list,
                    cancellationToken);
                var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
                var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
                var entityPermissions = await BuildEntityPermissionsAsync(runtime.Tenant, runtime.Session, [list.Entity], permissions, cancellationToken);
                return Results.Content(RenderEntityList(runtime.Tenant, runtime.Session, visibleEntities, list, query, entityPermissions[list.Entity.Key]), "text/html");
            }
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(RenderNotFound("Entity not found", "The generated entity is not available in this tenant."));
            }
            catch (RuntimeRecordValidationException ex)
            {
                return Results.BadRequest(Page("Invalid query", RenderErrors(ex.Errors)));
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        app.MapGet("/entities/{entityKey}/new", async (
            string entityKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "create", cancellationToken);
            if (authorized.Error is not null)
            {
                return authorized.Error;
            }

            var pageError = await AuthorizePageAsync(
                runtime,
                records,
                permissions,
                "entity_create",
                authorized.Entity!.Id,
                cancellationToken);
            if (pageError is not null)
            {
                return pageError;
            }

            var visibleEntity = await permissions.FilterReadableEntityAsync(
                runtime.Tenant,
                runtime.User!,
                authorized.Entity,
                cancellationToken);

            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var entityPermissions = await BuildEntityPermissionsAsync(runtime.Tenant, runtime.Session, [visibleEntity], permissions, cancellationToken);
            var relationOptions = await records.ListRelationOptionsAsync(
                runtime.Tenant,
                runtime.User!,
                visibleEntity,
                cancellationToken);
            return Results.Content(RenderEntityForm(runtime.Tenant, runtime.Session, visibleEntities, visibleEntity, null, relationOptions, entityPermissions[visibleEntity.Key], [], []), "text/html");
        });

        app.MapGet("/entities/{entityKey}/{recordId:long}", async (
            string entityKey,
            long recordId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimeActionService actions,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "read", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var pageError = await AuthorizePageAsync(
                    runtime,
                    records,
                    permissions,
                    "entity_detail",
                    authorized.Entity!.Id,
                    cancellationToken);
                if (pageError is not null)
                {
                    return pageError;
                }

                var includeArchived = await permissions.IsAllowedAsync(
                    runtime.Tenant,
                    runtime.User!,
                    "restore",
                    authorized.Entity!,
                    cancellationToken);
                var record = await records.GetRecordAsync(
                    runtime.Tenant,
                    runtime.User!,
                    authorized.Entity!.Key,
                    recordId,
                    includeArchived,
                    cancellationToken);
                if (record is null)
                {
                    return Results.NotFound(RenderNotFound("Record not found", "The generated record no longer exists."));
                }

                var visibleEntity = await permissions.FilterReadableEntityAsync(
                    runtime.Tenant,
                    runtime.User!,
                    authorized.Entity,
                    cancellationToken);
                record = await permissions.FilterReadableRecordFieldsAsync(
                    runtime.Tenant,
                    runtime.User!,
                    authorized.Entity,
                    record,
                    cancellationToken);

                var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
                var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
                var entityPermissions = await BuildEntityPermissionsAsync(runtime.Tenant, runtime.Session, [visibleEntity], permissions, cancellationToken);
                var relationOptions = await records.ListRelationOptionsAsync(
                    runtime.Tenant,
                    runtime.User!,
                    visibleEntity,
                    cancellationToken);
                var user = runtime.User!;
                var canStartWorkflow = await permissions.IsAllowedAsync(
                    runtime.Tenant,
                    user,
                    "start_workflow",
                    authorized.Entity,
                    cancellationToken);
                var recordActions = canStartWorkflow && record.GetValueOrDefault("archived_at") is null
                    ? await workflows.ListRecordActionsAsync(runtime.Tenant, user, authorized.Entity.Key, recordId, cancellationToken)
                    : [];
                if (record.GetValueOrDefault("archived_at") is null)
                {
                    recordActions = recordActions
                        .Concat(await actions.ListRecordActionsAsync(runtime.Tenant, user, authorized.Entity.Key, recordId, cancellationToken))
                        .ToArray();
                }
                var instances = await workflows.ListRecordInstancesAsync(
                    runtime.Tenant,
                    user,
                    authorized.Entity.Key,
                    recordId,
                    cancellationToken: cancellationToken);
                var workflowDetails = await LoadEmbeddedWorkflowDetailsAsync(
                    instances,
                    (instanceId, token) => workflows.GetInstanceAsync(runtime.Tenant, user, instanceId, token),
                    cancellationToken);

                return Results.Content(RenderEntityForm(runtime.Tenant, runtime.Session, visibleEntities, visibleEntity, record, relationOptions, entityPermissions[visibleEntity.Key], recordActions, workflowDetails), "text/html");
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        app.MapGet("/reports", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeReportService reports,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            var pageError = await AuthorizePageAsync(runtime, records, permissions, "reports", "*", cancellationToken);
            if (pageError is not null)
            {
                return pageError;
            }

            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var reportDefinitions = await reports.ListReportsAsync(runtime.Tenant, cancellationToken);
            var visibleReports = await FilterReadableReportsAsync(runtime.Tenant, runtime.User!, reportDefinitions, records, permissions, cancellationToken);
            return Results.Content(RenderReportsList(runtime.Tenant, runtime.Session, visibleEntities, visibleReports), "text/html");
        });

        app.MapGet("/reports/{reportKey}", async (
            string reportKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeReportService reports,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            var authorized = await AuthorizeReportAsync(runtime, reports, records, permissions, reportKey, cancellationToken);
            if (authorized.Error is not null)
            {
                return authorized.Error;
            }

            var pageError = await AuthorizePageAsync(
                runtime,
                records,
                permissions,
                "report_detail",
                authorized.Report!.Key,
                cancellationToken);
            if (pageError is not null)
            {
                return pageError;
            }

            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var query = BuildReportQuery(httpContext.Request);
            RuntimeReportResult result;
            try
            {
                result = await reports.ExecuteReportAsync(
                    runtime.Tenant,
                    runtime.User!,
                    authorized.Report!,
                    authorized.Entity!,
                    query,
                    cancellationToken);
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditReportPermissionDeniedAsync(records, runtime, authorized.Report!, cancellationToken);
                return Results.StatusCode(403);
            }

            await AuditReportViewedAsync(records, runtime, authorized.Report!, cancellationToken);
            return Results.Content(RenderReportDetail(runtime.Tenant, runtime.Session, visibleEntities, result, query), "text/html");
        });

        app.MapGet("/tasks", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var user = runtime.User!;
            var tasks = await workflows.ListTasksAsync(runtime.Tenant, user, includeCompleted: false, cancellationToken);
            var visibleTasks = await FilterReadableTasksAsync(runtime.Tenant, user, tasks, records, permissions, cancellationToken);
            return Results.Content(RenderTasksPage(runtime.Tenant, runtime.Session, visibleEntities, visibleTasks), "text/html");
        });

        app.MapGet("/tasks/{id:long}", async (
            long id,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            try
            {
                var detail = await workflows.GetTaskRecordAsync(
                    runtime.Tenant,
                    runtime.User!,
                    id,
                    cancellationToken);
                if (detail is null)
                {
                    return Results.Content(
                        RenderNotFound(
                            "Task not found",
                            "The workflow task or its evidence record no longer exists."),
                        "text/html",
                        statusCode: StatusCodes.Status404NotFound);
                }

                var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
                var visibleEntities = await FilterReadableEntitiesAsync(
                    runtime.Tenant,
                    runtime.Session,
                    entities,
                    permissions,
                    cancellationToken);
                return Results.Content(
                    RenderTaskDetailPage(runtime.Tenant, runtime.Session, visibleEntities, detail),
                    "text/html");
            }
            catch (RuntimeWorkflowPermissionException)
            {
                await AuditPermissionDeniedAsync(
                    records,
                    runtime,
                    "read_task",
                    "workflow_task_evidence",
                    cancellationToken);
                return Results.Content(
                    RenderNotFound(
                        "Task not found",
                        "The workflow task or its evidence record no longer exists."),
                    "text/html",
                    statusCode: StatusCodes.Status404NotFound);
            }
        });

        app.MapGet("/notifications", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            var canIncludeAll = await permissions.IsAllowedAsync(
                runtime.Tenant,
                runtime.User!,
                "admin",
                entity: null,
                cancellationToken: cancellationToken);
            var includeAll = canIncludeAll && string.Equals(
                httpContext.Request.Query["includeAll"].ToString(),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var notifications = await workflows.ListNotificationsAsync(runtime.Tenant, runtime.User!, includeAll, cancellationToken);
            return Results.Content(
                RenderNotificationsPage(runtime.Tenant, runtime.Session, visibleEntities, notifications, includeAll, canIncludeAll),
                "text/html");
        });

        app.MapGet("/workflow-instances/{id:long}", async (
            long id,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            try
            {
                var detail = await workflows.GetInstanceAsync(runtime.Tenant, runtime.User!, id, cancellationToken);
                if (detail is null)
                {
                    return Results.NotFound(RenderNotFound("Workflow not found", "The workflow instance no longer exists."));
                }

                var entity = await records.GetEntityAsync(runtime.Tenant, detail.Instance.EntityId, cancellationToken);
                if (entity is not null &&
                    !await permissions.IsAllowedAsync(runtime.Tenant, runtime.User!, "read_task", entity, cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "read_task", detail.Instance.EntityId, cancellationToken);
                    return Results.StatusCode(403);
                }

                var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
                var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
                return Results.Content(RenderWorkflowInstancePage(runtime.Tenant, runtime.Session, visibleEntities, detail), "text/html");
            }
            catch (RuntimeWorkflowPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read_task", "workflow_instances", cancellationToken);
                return Results.StatusCode(403);
            }
        });

        app.MapGet("/settings/users", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var runtimeUsers = await users.ListUsersAsync(runtime.Tenant, cancellationToken);
            var roles = await users.ListRolesAsync(runtime.Tenant, cancellationToken);
            var employees = await SafeLookupAsync(
                records,
                runtime.Tenant,
                runtime.User!,
                "employees",
                "",
                100,
                cancellationToken);
            return Results.Content(RenderUsersPage(runtime.Tenant, runtime.Session, visibleEntities, runtimeUsers, roles, employees, null), "text/html");
        });

        app.MapGet("/settings/users/invite", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var roles = await users.ListRolesAsync(runtime.Tenant, cancellationToken);
            var employees = await SafeLookupAsync(
                records,
                runtime.Tenant,
                runtime.User!,
                "employees",
                "",
                100,
                cancellationToken);
            return Results.Content(RenderInviteUserPage(runtime.Tenant, runtime.Session, visibleEntities, roles, employees), "text/html");
        });

        app.MapGet("/settings/deployment", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeDatabaseService runtimeDatabase,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound);
            }

            if (runtime.Session is null)
            {
                return Results.Redirect("/login");
            }

            if (!await permissions.IsAllowedAsync(runtime.Tenant, runtime.User!, "admin", null, cancellationToken))
            {
                await AuditPermissionDeniedAsync(records, runtime, "admin", "settings", cancellationToken);
                return Results.StatusCode(403);
            }

            var metadata = await runtimeDatabase.ReadMetadataAsync(runtime.Tenant, cancellationToken);
            var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
            var visibleEntities = await FilterReadableEntitiesAsync(runtime.Tenant, runtime.Session, entities, permissions, cancellationToken);
            var events = await records.ListAuditEventsAsync(runtime.Tenant, 25, cancellationToken);
            var history = await runtimeDatabase.ReadDeploymentHistoryAsync(runtime.Tenant, 25, cancellationToken);
            return Results.Content(RenderDeploymentInfo(runtime.Tenant, runtime.Session, metadata, visibleEntities, events, history), "text/html");
        });

        return app;
    }

    private static async Task<IReadOnlyList<RuntimeDashboardActivityPoint>> BuildRecordActivityAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IRuntimeRecordService records,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var firstDay = today.AddDays(-13);
        var counts = Enumerable.Range(0, 14)
            .ToDictionary(offset => firstDay.AddDays(offset), _ => 0L);

        foreach (var entity in entities.Take(16))
        {
            var page = await records.ListRecordsAsync(
                tenant,
                user,
                entity.Key,
                new RuntimeRecordQuery(Page: 1, PageSize: 100, SortBy: "created_at", SortDirection: "desc"),
                cancellationToken);
            foreach (var record in page.Records)
            {
                if (!TryReadRecordDay(record.GetValueOrDefault("created_at"), out var day) ||
                    day < firstDay || day > today)
                {
                    continue;
                }

                counts[day]++;
            }
        }

        return counts.Select(item => new RuntimeDashboardActivityPoint(item.Key, item.Value)).ToArray();
    }

    private static bool TryReadRecordDay(object? value, out DateOnly day)
    {
        switch (value)
        {
            case DateTimeOffset dateTimeOffset:
                day = DateOnly.FromDateTime(dateTimeOffset.UtcDateTime);
                return true;
            case DateTime dateTime:
                day = DateOnly.FromDateTime(dateTime.ToUniversalTime());
                return true;
            case string text when DateTimeOffset.TryParse(text, out var parsed):
                day = DateOnly.FromDateTime(parsed.UtcDateTime);
                return true;
            default:
                day = default;
                return false;
        }
    }

    internal static async Task<IReadOnlyList<RuntimeWorkflowInstanceDetail>> LoadEmbeddedWorkflowDetailsAsync(
        IReadOnlyList<RuntimeWorkflowInstance> instances,
        Func<long, CancellationToken, Task<RuntimeWorkflowInstanceDetail?>> loadDetail,
        CancellationToken cancellationToken)
    {
        var details = new List<RuntimeWorkflowInstanceDetail>(instances.Count);
        foreach (var instance in instances)
        {
            try
            {
                var detail = await loadDetail(instance.Id, cancellationToken);
                if (detail is not null)
                {
                    details.Add(detail);
                }
            }
            catch (RuntimeWorkflowPermissionException)
            {
                // A role or record scope may be revoked after the authorized
                // list snapshot. Embedded history should disappear rather than
                // turn an otherwise authorized record page into a 500.
            }
        }

        return details;
    }
}
