using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;

public static class WorkflowRoutes
{
    public static IEndpointRouteBuilder MapWorkflowRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/workflows", async (
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

            var user = runtime.User!;
            var visible = new List<RuntimeWorkflowDefinition>();
            foreach (var workflow in await workflows.ListWorkflowsAsync(runtime.Tenant!, cancellationToken))
            {
                var entity = await records.GetEntityAsync(runtime.Tenant!, workflow.Entity, cancellationToken);
                if (entity is not null &&
                    await permissions.IsAllowedAsync(runtime.Tenant!, user, "read", entity, cancellationToken) &&
                    await permissions.IsAllowedAsync(runtime.Tenant!, user, "start_workflow", entity, cancellationToken))
                {
                    visible.Add(workflow);
                }
            }

            return Results.Ok(visible);
        });

        app.MapGet("/api/runtime/workflow-instances", async (
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

            var user = runtime.User!;
            var instances = await workflows.ListInstancesAsync(runtime.Tenant!, user, cancellationToken);
            var visible = new List<RuntimeWorkflowInstance>();
            foreach (var instance in instances)
            {
                var entity = await records.GetEntityAsync(runtime.Tenant!, instance.EntityId, cancellationToken);
                if (entity is not null && await permissions.IsAllowedAsync(runtime.Tenant!, user, "read_task", entity, cancellationToken))
                {
                    visible.Add(instance);
                }
            }

            return Results.Ok(visible);
        });

        app.MapGet("/api/runtime/workflow-instances/{id:long}", async (
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
                var detail = await workflows.GetInstanceAsync(runtime.Tenant!, runtime.User!, id, cancellationToken);
                if (detail is null)
                {
                    return Results.NotFound(new { message = "Workflow instance was not found." });
                }

                var entity = await records.GetEntityAsync(runtime.Tenant!, detail.Instance.EntityId, cancellationToken);
                if (entity is not null &&
                    !await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, "read_task", entity, cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "read_task", detail.Instance.EntityId, cancellationToken);
                    return Results.StatusCode(403);
                }

                return Results.Ok(detail);
            }
            catch (RuntimeWorkflowPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read_task", "workflow_instances", cancellationToken);
                return Results.StatusCode(403);
            }
        });

        return app;
    }
}
