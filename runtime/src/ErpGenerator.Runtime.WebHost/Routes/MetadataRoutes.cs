using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;

public static class MetadataRoutes
{
    public static IEndpointRouteBuilder MapMetadataRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/metadata", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeDatabaseService runtimeDatabase,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var metadata = await runtimeDatabase.ReadMetadataAsync(runtime.Tenant!, cancellationToken);
            return Results.Ok(metadata);
        });

        return app;
    }
}
