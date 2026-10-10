using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;

public static class AuditRoutes
{
    public static IEndpointRouteBuilder MapAuditRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/audit/events", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            if (!await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, "admin", null, cancellationToken))
            {
                await AuditPermissionDeniedAsync(records, runtime, "admin", "audit.events", cancellationToken);
                return Results.StatusCode(403);
            }

            var limit = int.TryParse(httpContext.Request.Query["limit"], out var parsedLimit) ? parsedLimit : 50;
            return Results.Ok(await records.ListAuditEventsAsync(runtime.Tenant!, limit, cancellationToken));
        });

        return app;
    }
}
