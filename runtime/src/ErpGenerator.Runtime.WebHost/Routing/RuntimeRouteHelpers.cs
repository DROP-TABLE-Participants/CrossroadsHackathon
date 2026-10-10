using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

public static class RuntimeRouteHelpers
{
    public static async Task<ResolvedTenantEnvironment?> ResolveTenantAsync(
        HttpContext httpContext,
        IControlPlaneClient controlPlane,
        CancellationToken cancellationToken)
    {
        var host = httpContext.Request.Headers.Host.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(host))
        {
            host = httpContext.Request.Host.Value;
        }

        var tenant = string.IsNullOrWhiteSpace(host)
            ? null
            : await controlPlane.ResolveTenantAsync(host, cancellationToken);
        return tenant?.Status == "ready" ? tenant : null;
    }

    public static async Task<RuntimeRouteContext> ResolveAuthenticatedRuntimeAsync(
        HttpContext httpContext,
        IControlPlaneClient controlPlane,
        IRuntimeSessionService sessions,
        CancellationToken cancellationToken)
    {
        var tenant = await ResolveTenantAsync(httpContext, controlPlane, cancellationToken);
        if (tenant is null)
        {
            return new RuntimeRouteContext(null, null, null, Results.NotFound(new { message = "Tenant host is not registered." }));
        }

        var session = await ResolveRuntimeSessionAsync(httpContext, tenant, sessions);
        if (session is null)
        {
            return new RuntimeRouteContext(tenant, null, null, Results.Unauthorized());
        }

        var users = httpContext.RequestServices.GetRequiredService<IRuntimeUserService>();
        var user = await users.GetActiveUserContextAsync(tenant, session.UserId, cancellationToken);
        if (user is null)
        {
            return new RuntimeRouteContext(tenant, null, null, Results.Unauthorized());
        }

        return new RuntimeRouteContext(tenant, session with { Role = user.Role }, user, null);
    }

    public static Task<RuntimeSession?> ResolveRuntimeSessionAsync(
        HttpContext httpContext,
        ResolvedTenantEnvironment tenant,
        IRuntimeSessionService sessions)
    {
        var cookie = httpContext.Request.Cookies["erpgenerator_runtime_session"];
        return Task.FromResult(sessions.ValidateSession(cookie, tenant.TenantStableId, tenant.Host));
    }

    public static RuntimeUserContext ToUserContext(RuntimeSession session) =>
        new(session.UserId, session.Email, session.Role);

    public static async Task<IReadOnlyList<RuntimeEntityDefinition>> FilterReadableEntitiesAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IRuntimePermissionService permissions,
        CancellationToken cancellationToken)
    {
        var user = ToUserContext(session);
        var visible = new List<RuntimeEntityDefinition>();
        foreach (var entity in entities)
        {
            if (await permissions.IsAllowedAsync(tenant, user, "read", entity, cancellationToken))
            {
                visible.Add(await permissions.FilterReadableEntityAsync(tenant, user, entity, cancellationToken));
            }
        }

        return visible;
    }

    public static async Task<IReadOnlyList<RuntimeReportDefinition>> FilterReadableReportsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeReportDefinition> reports,
        IRuntimeRecordService records,
        IRuntimePermissionService permissions,
        CancellationToken cancellationToken)
    {
        var visible = new List<RuntimeReportDefinition>();
        foreach (var report in reports)
        {
            var requiredEntities = await ResolveReportEntitiesAsync(tenant, report, records, cancellationToken);
            if (requiredEntities is not null &&
                await CanReadAllReportEntitiesAsync(tenant, user, requiredEntities, permissions, cancellationToken) &&
                await CanReadReportFieldsAsync(tenant, user, report, requiredEntities, permissions, cancellationToken))
            {
                visible.Add(report);
            }
        }

        return visible;
    }

    public static async Task<RuntimeReportAuthorizationResult> AuthorizeReportAsync(
        RuntimeRouteContext runtime,
        IRuntimeReportService reports,
        IRuntimeRecordService records,
        IRuntimePermissionService permissions,
        string reportKey,
        CancellationToken cancellationToken)
    {
        var report = await reports.GetReportAsync(runtime.Tenant!, reportKey, cancellationToken);
        if (report is null)
        {
            return new RuntimeReportAuthorizationResult(null, null, Results.NotFound(new { message = "Report was not found." }));
        }

        var entity = await records.GetEntityAsync(runtime.Tenant!, report.SourceEntity, cancellationToken);
        if (entity is null)
        {
            return new RuntimeReportAuthorizationResult(report, null, Results.NotFound(new { message = "Report source entity was not found." }));
        }

        if (!await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, "read", entity, cancellationToken))
        {
            await AuditReportPermissionDeniedAsync(records, runtime, report, cancellationToken);
            return new RuntimeReportAuthorizationResult(report, entity, Results.StatusCode(403));
        }

        foreach (var targetEntityKey in RequiredReportEntityKeys(report).Skip(1))
        {
            var targetEntity = await records.GetEntityAsync(runtime.Tenant!, targetEntityKey, cancellationToken);
            if (targetEntity is null)
            {
                return new RuntimeReportAuthorizationResult(
                    report,
                    entity,
                    Results.NotFound(new { message = $"Report joined entity '{targetEntityKey}' was not found." }));
            }

            if (!await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, "read", targetEntity, cancellationToken))
            {
                await AuditReportPermissionDeniedAsync(records, runtime, report, cancellationToken);
                return new RuntimeReportAuthorizationResult(report, entity, Results.StatusCode(403));
            }
        }

        var reportEntities = await ResolveReportEntitiesAsync(
            runtime.Tenant!,
            report,
            records,
            cancellationToken);
        if (reportEntities is null ||
            !await CanReadReportFieldsAsync(
                runtime.Tenant!,
                runtime.User!,
                report,
                reportEntities,
                permissions,
                cancellationToken))
        {
            await AuditReportPermissionDeniedAsync(records, runtime, report, cancellationToken);
            return new RuntimeReportAuthorizationResult(report, entity, Results.StatusCode(403));
        }

        return new RuntimeReportAuthorizationResult(report, entity, null);
    }

    public static async Task<IReadOnlyList<RuntimeDashboardMetric>> BuildDashboardWidgetsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IRuntimeReportService reports,
        IRuntimeRecordService records,
        IRuntimePermissionService permissions,
        CancellationToken cancellationToken)
    {
        var dashboard = await reports.GetDashboardAsync(tenant, cancellationToken);
        var metrics = new List<RuntimeDashboardMetric>();
        foreach (var widget in dashboard.Widgets.Where(widget => widget.Type.Equals("metric", StringComparison.OrdinalIgnoreCase)))
        {
            var report = await reports.GetReportAsync(tenant, widget.Report, cancellationToken);
            if (report is null)
            {
                continue;
            }

            var requiredEntities = await ResolveReportEntitiesAsync(tenant, report, records, cancellationToken);
            if (requiredEntities is null ||
                !await CanReadAllReportEntitiesAsync(tenant, user, requiredEntities, permissions, cancellationToken) ||
                !await CanReadReportFieldsAsync(tenant, user, report, requiredEntities, permissions, cancellationToken))
            {
                continue;
            }

            var entity = requiredEntities[0];

            var reportQuery = widget.Key.Contains("this_month", StringComparison.OrdinalIgnoreCase)
                ? new RuntimeReportQuery(new DateOnly(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1), DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime), 250)
                : new RuntimeReportQuery(Limit: 250);
            RuntimeReportResult result;
            try
            {
                result = await reports.ExecuteReportAsync(tenant, user, report, entity, reportQuery, cancellationToken);
            }
            catch (RuntimeRecordPermissionException)
            {
                continue;
            }

            var value = ExtractDashboardMetric(result, widget.Metric);
            metrics.Add(new RuntimeDashboardMetric(widget.Key, widget.Label, value, report.Key));
        }

        return metrics;
    }

    private static IReadOnlyList<string> RequiredReportEntityKeys(RuntimeReportDefinition report) =>
        new[] { report.SourceEntity }
            .Concat((report.Joins ?? []).Select(join => join.TargetEntity))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task<IReadOnlyList<RuntimeEntityDefinition>?> ResolveReportEntitiesAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeReportDefinition report,
        IRuntimeRecordService records,
        CancellationToken cancellationToken)
    {
        var entities = new List<RuntimeEntityDefinition>();
        foreach (var entityKey in RequiredReportEntityKeys(report))
        {
            var entity = await records.GetEntityAsync(tenant, entityKey, cancellationToken);
            if (entity is null)
            {
                return null;
            }

            entities.Add(entity);
        }

        return entities;
    }

    private static async Task<bool> CanReadAllReportEntitiesAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IRuntimePermissionService permissions,
        CancellationToken cancellationToken)
    {
        foreach (var entity in entities)
        {
            if (!await permissions.IsAllowedAsync(tenant, user, "read", entity, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> CanReadReportFieldsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeReportDefinition report,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IRuntimePermissionService permissions,
        CancellationToken cancellationToken)
    {
        var entityById = entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        if (!entityById.TryGetValue(report.SourceEntity, out var source))
        {
            source = entities.FirstOrDefault(entity =>
                entity.Key.Equals(report.SourceEntity, StringComparison.OrdinalIgnoreCase) ||
                entity.Name.Equals(report.SourceEntity, StringComparison.OrdinalIgnoreCase));
        }

        if (source is null)
        {
            return false;
        }

        var aliases = new Dictionary<string, RuntimeEntityDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = source
        };
        foreach (var join in report.Joins ?? [])
        {
            var target = entities.FirstOrDefault(entity =>
                entity.Id.Equals(join.TargetEntity, StringComparison.OrdinalIgnoreCase) ||
                entity.Key.Equals(join.TargetEntity, StringComparison.OrdinalIgnoreCase) ||
                entity.Name.Equals(join.TargetEntity, StringComparison.OrdinalIgnoreCase));
            if (target is null || !aliases.TryGetValue(join.FromAlias, out var from))
            {
                return false;
            }

            if (!await permissions.IsFieldAllowedAsync(
                    tenant,
                    user,
                    "read",
                    from,
                    join.RelationField,
                    cancellationToken))
            {
                return false;
            }

            aliases[join.Alias] = target;
        }

        var fieldRefs = report.GroupBy.Select(group => group.Field)
            .Concat(report.Metrics.Select(metric => metric.Field))
            .Concat(report.Filters.Select(filter => filter.Field))
            .Concat(report.Metrics.Where(metric => metric.Filter is not null).Select(metric => metric.Filter!.Field))
            .Where(field => !string.IsNullOrWhiteSpace(field));
        foreach (var fieldRef in fieldRefs)
        {
            var parts = fieldRef!.Split('.', 2, StringSplitOptions.TrimEntries);
            var alias = parts.Length == 2 ? parts[0] : "source";
            var field = parts.Length == 2 ? parts[1] : parts[0];
            if (!aliases.TryGetValue(alias, out var entity) ||
                !await permissions.IsFieldAllowedAsync(
                    tenant,
                    user,
                    "read",
                    entity,
                    field,
                    cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    public static object? ExtractDashboardMetric(RuntimeReportResult result, string metricKey)
    {
        if (string.IsNullOrWhiteSpace(metricKey))
        {
            return null;
        }

        decimal numericTotal = 0;
        var numericCount = 0;
        object? first = null;
        foreach (var row in result.Rows)
        {
            var value = row.GetValueOrDefault(metricKey);
            first ??= value;
            if (decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric))
            {
                numericTotal += numeric;
                numericCount++;
            }
        }

        return numericCount > 0 ? numericTotal : first;
    }

    public static async Task<IReadOnlyDictionary<string, RuntimeEntityUiPermissions>> BuildEntityPermissionsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        IRuntimePermissionService permissions,
        CancellationToken cancellationToken)
    {
        var user = ToUserContext(session);
        var result = new Dictionary<string, RuntimeEntityUiPermissions>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            result[entity.Key] = new RuntimeEntityUiPermissions(
                await permissions.IsAllowedAsync(tenant, user, "read", entity, cancellationToken),
                await permissions.IsAllowedAsync(tenant, user, "create", entity, cancellationToken),
                await permissions.IsAllowedAsync(tenant, user, "update", entity, cancellationToken),
                await permissions.IsAllowedAsync(tenant, user, "archive", entity, cancellationToken),
                await permissions.IsAllowedAsync(tenant, user, "restore", entity, cancellationToken));
        }

        return result;
    }

    public static async Task<RuntimeAuthorizationResult> AuthorizeEntityAsync(
        RuntimeRouteContext runtime,
        IRuntimeRecordService records,
        IRuntimePermissionService permissions,
        string entityKey,
        string action,
        CancellationToken cancellationToken)
    {
        var entity = await records.GetEntityAsync(runtime.Tenant!, entityKey, cancellationToken);
        if (entity is null)
        {
            return new RuntimeAuthorizationResult(null, Results.NotFound(new { message = "Entity was not found." }));
        }

        if (!await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, action, entity, cancellationToken))
        {
            await AuditPermissionDeniedAsync(records, runtime, action, entity.Key, cancellationToken);
            return new RuntimeAuthorizationResult(entity, Results.StatusCode(403));
        }

        return new RuntimeAuthorizationResult(entity, null);
    }

    public static async Task<IResult?> AuthorizePageAsync(
        RuntimeRouteContext runtime,
        IRuntimeRecordService records,
        IRuntimePermissionService permissions,
        string page,
        string resource,
        CancellationToken cancellationToken)
    {
        if (await permissions.IsPageAllowedAsync(
                runtime.Tenant!,
                runtime.User!,
                page,
                resource,
                cancellationToken))
        {
            return null;
        }

        await AuditPermissionDeniedAsync(records, runtime, $"page:{page}", resource, cancellationToken);
        return Results.StatusCode(403);
    }

    public static async Task<IResult?> RequireRuntimeAdminAsync(
        IRuntimeRecordService records,
        IRuntimePermissionService permissions,
        RuntimeRouteContext runtime,
        CancellationToken cancellationToken)
    {
        if (await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, "admin", null, cancellationToken))
        {
            return null;
        }

        await AuditPermissionDeniedAsync(records, runtime, "admin", "runtime_users", cancellationToken);
        return Results.StatusCode(403);
    }

    public static async Task AuditPermissionDeniedAsync(
        IRuntimeRecordService records,
        RuntimeRouteContext runtime,
        string action,
        string? entityKey,
        CancellationToken cancellationToken)
    {
        await records.RecordAuditEventAsync(
            runtime.Tenant!,
            runtime.User,
            "permission.denied",
            "Runtime permission denied.",
            new Dictionary<string, object?>
            {
                ["action"] = action,
                ["entity"] = entityKey
            },
            cancellationToken);
    }

    public static async Task AuditReportViewedAsync(
        IRuntimeRecordService records,
        RuntimeRouteContext runtime,
        RuntimeReportDefinition report,
        CancellationToken cancellationToken)
    {
        await records.RecordAuditEventAsync(
            runtime.Tenant!,
            runtime.User,
            "report.viewed",
            "Runtime report viewed.",
            new Dictionary<string, object?>
            {
                ["report"] = report.Key,
                ["sourceEntity"] = report.SourceEntity
            },
            cancellationToken);
    }

    public static async Task AuditReportPermissionDeniedAsync(
        IRuntimeRecordService records,
        RuntimeRouteContext runtime,
        RuntimeReportDefinition report,
        CancellationToken cancellationToken)
    {
        await records.RecordAuditEventAsync(
            runtime.Tenant!,
            runtime.User,
            "report.permission_denied",
            "Runtime report permission denied.",
            new Dictionary<string, object?>
            {
                ["report"] = report.Key,
                ["sourceEntity"] = report.SourceEntity
            },
            cancellationToken);
    }

    public static async Task<IResult> CompleteWorkflowTaskAsync(
        long taskId,
        string action,
        string? reason,
        HttpContext httpContext,
        IControlPlaneClient controlPlane,
        RuntimeUsageEventForwarder usageEvents,
        IRuntimeRecordService records,
        IRuntimeWorkflowService workflows,
        IRuntimePermissionService permissions,
        IRuntimeSessionService sessions,
        CancellationToken cancellationToken)
    {
        var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
        if (runtime.Error is not null)
        {
            return runtime.Error;
        }

        var user = runtime.User!;
        try
        {
            var task = await workflows.GetTaskAsync(runtime.Tenant!, user, taskId, cancellationToken);
            if (task is null)
            {
                return Results.NotFound(new { message = "Workflow task was not found." });
            }

            var entity = await records.GetEntityAsync(runtime.Tenant!, task.EntityId, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Task entity was not found." });
            }

            if (!await permissions.IsAllowedAsync(runtime.Tenant!, user, action, entity, cancellationToken))
            {
                await AuditPermissionDeniedAsync(records, runtime, action, entity.Key, cancellationToken);
                return Results.StatusCode(403);
            }

            var result = action == "approve_task"
                ? await workflows.ApproveTaskAsync(runtime.Tenant!, user, taskId, reason, cancellationToken)
                : await workflows.RejectTaskAsync(runtime.Tenant!, user, taskId, reason, cancellationToken);
            await usageEvents.ForwardAsync(
                runtime,
                action == "approve_task" ? "workflow_task_approved" : "workflow_task_rejected",
                entityKey: result.Task.EntityId,
                recordId: result.Task.RecordId.ToString(),
                workflowKey: result.Task.WorkflowId,
                metadata: new Dictionary<string, object?>
                {
                    ["taskId"] = result.Task.Id,
                    ["stepId"] = result.Task.StepId
                },
                cancellationToken: cancellationToken);
            return Results.Ok(result);
        }
        catch (RuntimeWorkflowPermissionException)
        {
            await AuditPermissionDeniedAsync(records, runtime, action, "workflow_tasks", cancellationToken);
            return Results.NotFound(new { message = "Workflow task was not found." });
        }
        catch (RuntimeWorkflowTaskNotFoundException)
        {
            return Results.NotFound(new { message = "Workflow task was not found." });
        }
        catch (RuntimeWorkflowConflictException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
        catch (RuntimeWorkflowValidationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    public static async Task<IReadOnlyList<RuntimeWorkflowTask>> FilterReadableTasksAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeWorkflowTask> tasks,
        IRuntimeRecordService records,
        IRuntimePermissionService permissions,
        CancellationToken cancellationToken)
    {
        var visible = new List<RuntimeWorkflowTask>();
        foreach (var task in tasks)
        {
            var entity = await records.GetEntityAsync(tenant, task.EntityId, cancellationToken);
            if (entity is not null && await permissions.IsAllowedAsync(tenant, user, "read_task", entity, cancellationToken))
            {
                visible.Add(task);
            }
        }

        return visible;
    }

    public static async Task<IReadOnlyList<RuntimeLookupItem>> SafeLookupAsync(
        IRuntimeRecordService records,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        string search,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            return await records.LookupRecordsAsync(tenant, user, entityKey, search, limit, cancellationToken);
        }
        catch (Exception exception) when (exception is RuntimeEntityNotFoundException or RuntimeRecordPermissionException)
        {
            return [];
        }
    }

    public static RuntimeRecordQuery BuildRecordQuery(HttpRequest request)
    {
        var page = int.TryParse(request.Query["page"], out var parsedPage) ? parsedPage : 1;
        var pageSize = int.TryParse(request.Query["pageSize"], out var parsedPageSize) ? parsedPageSize : 25;
        return new RuntimeRecordQuery(
            page,
            pageSize,
            request.Query["sortBy"].ToString(),
            request.Query["sortDirection"].ToString(),
            request.Query["q"].ToString(),
            string.Equals(request.Query["includeArchived"].ToString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    public static RuntimeReportQuery BuildReportQuery(HttpRequest request)
    {
        var dateFrom = DateOnly.TryParse(request.Query["dateFrom"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedFrom)
            ? parsedFrom
            : (DateOnly?)null;
        var dateTo = DateOnly.TryParse(request.Query["dateTo"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTo)
            ? parsedTo
            : (DateOnly?)null;
        var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? parsedLimit : 100;
        return new RuntimeReportQuery(dateFrom, dateTo, limit);
    }

    public static string BuildReportCsv(RuntimeReportResult report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", report.Columns.Select(column => CsvCell(column.Label))));
        foreach (var row in report.Rows)
        {
            builder.AppendLine(string.Join(",", report.Columns.Select(column =>
            {
                var value = row.GetValueOrDefault($"{column.Key}__label") ?? row.GetValueOrDefault(column.Key);
                return CsvCell(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            })));
        }

        return builder.ToString();
    }

    public static string CsvCell(string value)
    {
        var normalized = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return normalized.Contains(',', StringComparison.Ordinal) ||
               normalized.Contains('"', StringComparison.Ordinal) ||
               normalized.Contains('\n', StringComparison.Ordinal) ||
               normalized.Contains('\r', StringComparison.Ordinal)
            ? $"\"{normalized}\""
            : normalized;
    }

    public static string SanitizeCsvFileName(string value)
    {
        var safe = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "report" : safe;
    }

    public static async Task<Dictionary<string, JsonElement>> ReadJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Request body must be a JSON object.");
        }

        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsInternalRequest(HttpContext httpContext, IConfiguration configuration)
    {
        var configuredSecret = configuration["Runtime:InternalSecret"];
        var providedSecret = httpContext.Request.Headers.TryGetValue("x-erpgenerator-internal-secret", out var provided)
            ? provided.ToString()
            : null;

        return IsInternalSecretAuthorized(providedSecret, configuredSecret);
    }

    public static bool IsInternalSecretAuthorized(string? providedSecret, string? configuredSecret)
    {
        if (string.IsNullOrWhiteSpace(configuredSecret) || string.IsNullOrWhiteSpace(providedSecret))
        {
            return false;
        }

        var configuredBytes = Encoding.UTF8.GetBytes(configuredSecret);
        var providedBytes = Encoding.UTF8.GetBytes(providedSecret);
        return configuredBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes);
    }

    public static string BuildActivationUrl(string runtimeBaseUrl, string token) =>
        $"{runtimeBaseUrl.TrimEnd('/')}/activate?token={WebUtility.UrlEncode(token)}";

    /// <summary>
    /// Activation URL that also carries the invited person's email and name so the
    /// signup form can pre-fill them (they don't retype what the admin entered).
    /// </summary>
    public static string BuildActivationUrl(string runtimeBaseUrl, string token, string? email, string? name)
    {
        var url = BuildActivationUrl(runtimeBaseUrl, token);
        if (!string.IsNullOrWhiteSpace(email))
        {
            url += $"&email={WebUtility.UrlEncode(email.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            url += $"&name={WebUtility.UrlEncode(name.Trim())}";
        }

        return url;
    }

    public static TenantEnvironmentKind? ParseDeploymentEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Equals("erp", StringComparison.OrdinalIgnoreCase)
            ? TenantEnvironmentKind.Erp
            : throw new ArgumentException("Environment must be erp.", nameof(value));
    }

    public static string ResolveSampleBundlePath(string contentRootPath, string? version)
    {
        var fileName = version?.Trim().ToLowerInvariant() switch
        {
            "manufacturing" or "manufacturing/v1" or "manufacturing-demo" => "manufacturing/v1",
            "it-services/v4" or "it-services" or "sample-folder-v4" => "it-services/v4",
            "0.4.0" or "v4" or "sample-erp-v4" => "sample-erp-v4.yaml",
            "0.3.0" or "v3" or "sample-erp-v3" => "sample-erp-v3.yaml",
            "0.2.0" or "v2" or "sample-erp-v2" => "sample-erp-v2.yaml",
            _ => "sample-erp.yaml"
        };

        return Path.Combine(contentRootPath, "..", "..", "bundles", fileName);
    }
}
