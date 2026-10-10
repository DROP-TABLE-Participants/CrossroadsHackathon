using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Runtime.WebHost.Observability;

public sealed class RequestCorrelationMiddleware(
    RequestDelegate next,
    ILogger<RequestCorrelationMiddleware> logger,
    IOptions<RequestCorrelationOptions> options)
{
    private readonly RequestCorrelationOptions options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = ResolveRequestId(context, options.HeaderName);
        RequestCorrelationContext.SetRequestId(context, requestId);
        context.Response.Headers[options.HeaderName] = requestId;

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.Value ?? string.Empty,
            ["host"] = context.Request.Host.Value
        });

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            logger.LogInformation(
                "Runtime request completed: {Method} {Path} responded {StatusCode} in {ElapsedMs:0.0} ms.",
                context.Request.Method,
                NormalizePath(context.Request.Path),
                context.Response.StatusCode,
                elapsedMs);
        }
    }

    private static string ResolveRequestId(HttpContext context, string headerName)
    {
        var provided = context.Request.Headers[headerName].FirstOrDefault();
        return string.IsNullOrWhiteSpace(provided)
            ? Guid.NewGuid().ToString("N")
            : provided.Trim();
    }

    public static string NormalizePath(PathString path) =>
        string.IsNullOrWhiteSpace(path.Value) ? "/" : path.Value;
}
