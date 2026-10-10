using ErpGenerator.Runtime.WebHost.Readiness;

public static class HealthRoutes
{
    public static IEndpointRouteBuilder MapHealthRoutes(this IEndpointRouteBuilder app)
    {
        static IResult RuntimeHealth() => Results.Ok(new
        {
            status = "ok",
            service = "erpgenerator-runtime"
        });

        app.MapGet("/health", RuntimeHealth);
        app.MapGet("/api/health", RuntimeHealth);
        app.MapGet("/ready", RuntimeReady);

        return app;
    }

    private static async Task<IResult> RuntimeReady(
        RuntimeControlPlaneReadinessProbe probe,
        CancellationToken cancellationToken)
    {
        var readiness = await probe.CheckAsync(cancellationToken);
        var body = new
        {
            status = readiness.Ready ? "ready" : "not_ready",
            service = "erpgenerator-runtime",
            checks = new { controlPlane = readiness.Status }
        };

        return readiness.Ready
            ? Results.Ok(body)
            : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
