using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;

public static class NotificationRoutes
{
    public static IEndpointRouteBuilder MapNotificationRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/notifications", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
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

            var includeAll = string.Equals(httpContext.Request.Query["includeAll"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (includeAll &&
                !await permissions.IsAllowedAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    "admin",
                    entity: null,
                    cancellationToken: cancellationToken))
            {
                includeAll = false;
            }

            return Results.Ok(await workflows.ListNotificationsAsync(runtime.Tenant!, runtime.User!, includeAll, cancellationToken));
        });

        app.MapPost("/api/runtime/notifications/{id:long}/read", async (
            long id,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeWorkflowService workflows,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var updated = await workflows.MarkNotificationReadAsync(runtime.Tenant!, runtime.User!, id, cancellationToken);
            return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Notification was not found." });
        });

        app.MapPost("/api/runtime/notifications/read-all", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeWorkflowService workflows,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var count = await workflows.MarkAllNotificationsReadAsync(runtime.Tenant!, runtime.User!, cancellationToken);
            return Results.Ok(new { count });
        });

        return app;
    }
}
