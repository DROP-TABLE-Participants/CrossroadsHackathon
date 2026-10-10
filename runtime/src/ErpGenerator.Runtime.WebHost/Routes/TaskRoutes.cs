using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

public static class TaskRoutes
{
    public static IEndpointRouteBuilder MapTaskRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/tasks", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var includeCompleted = string.Equals(httpContext.Request.Query["includeCompleted"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var user = runtime.User!;
            var tasks = await workflows.ListTasksAsync(runtime.Tenant!, user, includeCompleted, cancellationToken);
            return Results.Ok(await FilterReadableTasksAsync(runtime.Tenant!, user, tasks, records, permissions, cancellationToken));
        });

        app.MapGet("/api/runtime/tasks/{id:long}", async (
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
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var task = await workflows.GetTaskAsync(runtime.Tenant!, runtime.User!, id, cancellationToken);
                if (task is null)
                {
                    return Results.NotFound(new { message = "Workflow task was not found." });
                }

                var entity = await records.GetEntityAsync(runtime.Tenant!, task.EntityId, cancellationToken);
                if (entity is not null &&
                    !await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, "read_task", entity, cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "read_task", task.EntityId, cancellationToken);
                    return Results.StatusCode(403);
                }

                return Results.Ok(task);
            }
            catch (RuntimeWorkflowPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read_task", "workflow_tasks", cancellationToken);
                return Results.NotFound(new { message = "Workflow task was not found." });
            }
        });

        app.MapGet("/api/runtime/tasks/{id:long}/record", async (
            long id,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var detail = await workflows.GetTaskRecordAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    id,
                    cancellationToken);
                return detail is null
                    ? Results.NotFound(new { message = "Workflow task evidence was not found." })
                    : Results.Ok(detail);
            }
            catch (RuntimeWorkflowPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read_task", "workflow_task_evidence", cancellationToken);
                return Results.NotFound(new { message = "Workflow task evidence was not found." });
            }
        });

        app.MapPost("/api/runtime/tasks/{id:long}/approve", async (
            long id,
            WorkflowDecisionRequest request,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            return await CompleteWorkflowTaskAsync(
                id,
                "approve_task",
                request.Reason,
                httpContext,
                controlPlane,
                usageEvents,
                records,
                workflows,
                permissions,
                sessions,
                cancellationToken);
        });

        app.MapPost("/api/runtime/tasks/{id:long}/reject", async (
            long id,
            WorkflowDecisionRequest request,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            return await CompleteWorkflowTaskAsync(
                id,
                "reject_task",
                request.Reason,
                httpContext,
                controlPlane,
                usageEvents,
                records,
                workflows,
                permissions,
                sessions,
                cancellationToken);
        });

        return app;
    }
}
