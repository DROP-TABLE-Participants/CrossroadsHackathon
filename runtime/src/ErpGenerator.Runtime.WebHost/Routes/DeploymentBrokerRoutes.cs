using System.Text.Json;
using System.Text.Json.Serialization;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Services.DeploymentBroker;

public static class DeploymentBrokerRoutes
{
    public const string IdempotencyHeaderName = "x-erpgenerator-idempotency-key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IEndpointRouteBuilder MapDeploymentBrokerRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/deployment-broker/staging/deploy-curated", async (HttpContext context, IRuntimeDeploymentBrokerAuth auth, IRuntimeDeploymentBrokerService broker, CancellationToken ct) =>
        {
            if (!auth.IsAuthorized(context.Request.Headers[RuntimeDeploymentBrokerAuth.HeaderName].FirstOrDefault())) return Results.Unauthorized();
            var request = await ReadBoundedRequestAsync<RuntimeCuratedDeploymentRequest>(context.Request, ct);
            if (request is null || request.Target is null || request.Files is null || !HasMatchingIdempotencyKey(context.Request, request.OperationId)) return InvalidRequest();
            var response = await broker.DeployCuratedAsync(request, ct);
            return BrokerResult(response.Success, response);
        });
        app.MapPost("/internal/deployment-broker/guest-entry", async (HttpContext context, IRuntimeDeploymentBrokerAuth auth, IRuntimeDeploymentBrokerService broker, CancellationToken ct) =>
        {
            if (!auth.IsAuthorized(context.Request.Headers[RuntimeDeploymentBrokerAuth.HeaderName].FirstOrDefault())) return Results.Unauthorized();
            var request = await ReadBoundedRequestAsync<RuntimeGuestEntryRequest>(context.Request, ct);
            if (request is null || request.Target is null || !HasMatchingIdempotencyKey(context.Request, request.OperationId)) return InvalidRequest();
            return Results.Ok(await broker.CreateGuestEntryAsync(request, ct));
        });
        app.MapPost(
            "/internal/deployment-broker/staging/deploy",
            async (HttpContext context, IRuntimeDeploymentBrokerAuth auth, IRuntimeDeploymentBrokerService broker, CancellationToken cancellationToken) =>
            {
                if (!auth.IsAuthorized(context.Request.Headers[RuntimeDeploymentBrokerAuth.HeaderName].FirstOrDefault()))
                {
                    return Results.Unauthorized();
                }

                var request = await ReadBoundedRequestAsync<RuntimeStagingDeploymentBrokerRequest>(context.Request, cancellationToken);
                if (request is null || request.Target is null || request.Artifact is null || !HasMatchingIdempotencyKey(context.Request, request.OperationId))
                {
                    return InvalidRequest();
                }

                var response = await broker.DeployStagingAsync(request, cancellationToken);
                return BrokerResult(response.Success, response);
            });

        app.MapPost(
            "/internal/deployment-broker/staging/deploy-canonical",
            async (HttpContext context, IRuntimeDeploymentBrokerAuth auth, ICanonicalBundleDeploymentService service, CancellationToken cancellationToken) =>
            {
                if (!auth.IsAuthorized(context.Request.Headers[RuntimeDeploymentBrokerAuth.HeaderName].FirstOrDefault())) return Results.Unauthorized();
                var request = await ReadBoundedRequestAsync<CanonicalBundleDeploymentRequest>(context.Request, cancellationToken);
                if (request is null || request.Target is null || request.Bundle is null ||
                    !HasMatchingIdempotencyKey(context.Request, request.OperationId))
                {
                    return InvalidRequest();
                }
                var response = await service.DeployAsync(request, cancellationToken);
                return BrokerResult(response.Success, response);
            });

        app.MapPost(
            "/internal/deployment-broker/staging/reset",
            async (HttpContext context, IRuntimeDeploymentBrokerAuth auth, IRuntimeDeploymentBrokerService broker, CancellationToken cancellationToken) =>
            {
                if (!auth.IsAuthorized(context.Request.Headers[RuntimeDeploymentBrokerAuth.HeaderName].FirstOrDefault()))
                {
                    return Results.Unauthorized();
                }

                var request = await ReadBoundedRequestAsync<RuntimeStagingResetBrokerRequest>(context.Request, cancellationToken);
                if (request is null || request.Target is null || !HasMatchingIdempotencyKey(context.Request, request.OperationId))
                {
                    return InvalidRequest();
                }

                var response = await broker.ResetStagingAsync(request, cancellationToken);
                return BrokerResult(response.Success, response);
            });

        app.MapPost(
            "/internal/deployment-broker/production/promote",
            async (HttpContext context, IRuntimeDeploymentBrokerAuth auth, IRuntimeDeploymentBrokerService broker, CancellationToken cancellationToken) =>
            {
                if (!auth.IsAuthorized(context.Request.Headers[RuntimeDeploymentBrokerAuth.HeaderName].FirstOrDefault()))
                {
                    return Results.Unauthorized();
                }

                var request = await ReadBoundedRequestAsync<RuntimeProductionPromotionBrokerRequest>(context.Request, cancellationToken);
                if (request is null || request.StagingTarget is null || request.ProductionTarget is null || !HasMatchingIdempotencyKey(context.Request, request.OperationId))
                {
                    return InvalidRequest();
                }

                var response = await broker.PromoteProductionAsync(request, cancellationToken);
                return BrokerResult(response.Success, response);
            });

        app.MapPost(
            "/internal/deployment-broker/owner-activation",
            async (HttpContext context, IRuntimeDeploymentBrokerAuth auth, IRuntimeDeploymentBrokerService broker, CancellationToken cancellationToken) =>
            {
                if (!auth.IsAuthorized(context.Request.Headers[RuntimeDeploymentBrokerAuth.HeaderName].FirstOrDefault()))
                {
                    return Results.Unauthorized();
                }

                var request = await ReadBoundedRequestAsync<RuntimeOwnerActivationBrokerRequest>(context.Request, cancellationToken);
                if (request is null || request.Target is null || !HasMatchingIdempotencyKey(context.Request, request.OperationId))
                {
                    return InvalidRequest();
                }

                var response = await broker.CreateOwnerActivationAsync(request, cancellationToken);
                return BrokerResult(response.Success, response);
            });

        return app;
    }

    private static IResult BrokerResult<T>(bool success, T response) =>
        success
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status409Conflict);

    private static IResult InvalidRequest() =>
        Results.Json(
            new { failureCode = "invalid_request", safeError = "A bounded request with an exact idempotency key is required." },
            statusCode: StatusCodes.Status400BadRequest);

    private static bool HasMatchingIdempotencyKey(HttpRequest request, string operationId) =>
        !string.IsNullOrWhiteSpace(operationId) &&
        request.Headers.TryGetValue(IdempotencyHeaderName, out var header) &&
        string.Equals(header.ToString(), operationId, StringComparison.Ordinal);

    private static async Task<T?> ReadBoundedRequestAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > RuntimeDeploymentBrokerLimits.MaximumRequestBytes)
        {
            return default;
        }

        try
        {
            await using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await request.Body.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > RuntimeDeploymentBrokerLimits.MaximumRequestBytes)
                {
                    return default;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
