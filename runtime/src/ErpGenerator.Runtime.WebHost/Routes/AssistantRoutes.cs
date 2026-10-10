using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Shared.ArtificialIntelligence;
using static RuntimeRouteHelpers;

public static class AssistantRoutes
{
    public static IEndpointRouteBuilder MapAssistantRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runtime/assistant/query", async (
            ErpAssistantQuestion request,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            IErpAssistantService assistant,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Tenant is null)
            {
                return Results.NotFound(new { message = "This tenant could not be resolved." });
            }

            if (runtime.Session is null || runtime.User is null)
            {
                return Results.Unauthorized();
            }

            var question = request.Question?.Trim();
            if (string.IsNullOrWhiteSpace(question) || question.Length < 3 || question.Length > 2000)
            {
                return Results.BadRequest(new { message = "Ask a question between 3 and 2,000 characters." });
            }

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

            try
            {
                var entities = await records.ListEntitiesAsync(runtime.Tenant, cancellationToken);
                var visibleEntities = await FilterReadableEntitiesAsync(
                    runtime.Tenant,
                    runtime.Session,
                    entities,
                    permissions,
                    cancellationToken);
                var answer = await assistant.AskAsync(
                    runtime.Tenant,
                    runtime.User,
                    visibleEntities,
                    question,
                    new RuntimeAssistantContext(
                        RuntimeAssistantContextKinds.Dashboard,
                        httpContext.Request.Path.Value ?? "/"),
                    cancellationToken);
                return Results.Ok(answer);
            }
            catch (RuntimeAssistantConfigurationException)
            {
                return Results.Json(
                    new { message = "The ERP assistant is not configured yet." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(
                    new { message = "ErpGenerator AI took too long to answer. Please try a narrower question." },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception exception)
            {
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("ErpAssistant")
                    .LogError(exception, "ERP assistant query failed for tenant {Tenant}.", runtime.Tenant.TenantSlug);
                return Results.Json(
                    new { message = "The ERP assistant could not answer right now." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        return app;
    }
}

public sealed record ErpAssistantQuestion(string? Question);
