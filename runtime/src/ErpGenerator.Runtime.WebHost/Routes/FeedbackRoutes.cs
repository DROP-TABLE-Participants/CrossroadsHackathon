using System.Text.Json;
using System.Text.RegularExpressions;
using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

public static partial class FeedbackRoutes
{
    public static IEndpointRouteBuilder MapFeedbackRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runtime/feedback", async (
            HttpContext httpContext,
            RuntimeFeedbackRequest request,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var message = SanitizeText(request.Message, 2000);
            if (string.IsNullOrWhiteSpace(message))
            {
                return Results.BadRequest(new { message = "Feedback message is required." });
            }

            var category = SanitizeText(request.Category, 80);
            var entityKey = SanitizeText(request.EntityKey, 120);
            var recordId = SanitizeText(request.RecordId, 80);
            var pagePath = SanitizePath(request.PagePath);
            var metadata = SanitizeMetadata(request.Metadata);

            await records.RecordAuditEventAsync(
                runtime.Tenant!,
                runtime.User!,
                "feedback.submitted",
                "Runtime user submitted feedback.",
                new Dictionary<string, object?>
                {
                    ["category"] = category,
                    ["entityKey"] = entityKey,
                    ["recordId"] = recordId,
                    ["pagePath"] = pagePath,
                    ["messageLength"] = message.Length
                },
                cancellationToken);

            try
            {
                await controlPlane.SubmitRuntimeFeedbackAsync(
                    new RuntimeFeedbackCreate(
                        runtime.Tenant!.Host,
                        runtime.Tenant.TenantStableId,
                        runtime.Tenant.TenantSlug,
                        runtime.Tenant.Environment.ToString().ToLowerInvariant(),
                        runtime.User!.UserId.ToString(),
                        runtime.User.Email,
                        category,
                        message,
                        entityKey,
                        recordId,
                        pagePath,
                        metadata),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                httpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RuntimeFeedback")
                    .LogWarning(exception, "Runtime feedback submission failed for host {Host}.", runtime.Tenant!.Host);

                return Results.Json(
                    new { message = "Feedback could not be submitted right now." },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            await usageEvents.ForwardAsync(
                runtime,
                "feedback_submitted",
                entityKey,
                recordId,
                pagePath: pagePath,
                metadata: new Dictionary<string, object?>
                {
                    ["category"] = category,
                    ["messageLength"] = message.Length
                },
                cancellationToken: cancellationToken);

            return Results.Ok(new { ok = true });
        });

        return app;
    }

    private static IReadOnlyDictionary<string, object?> SanitizeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
        {
            return new Dictionary<string, object?>();
        }

        var safe = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in metadata.Take(20))
        {
            var safeKey = SanitizeText(key, 80);
            if (string.IsNullOrWhiteSpace(safeKey) || IsSensitiveKey(safeKey))
            {
                continue;
            }

            safe[safeKey] = value switch
            {
                null => null,
                string text => SanitizeText(text, 240),
                bool boolean => boolean,
                int or long or decimal or double or float => value,
                JsonElement element when element.ValueKind == JsonValueKind.String => SanitizeText(element.GetString(), 240),
                JsonElement element when element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                _ => SanitizeText(Convert.ToString(value), 240)
            };
        }

        return safe;
    }

    private static string SanitizePath(string? value)
    {
        var path = SanitizeText(value, 240);
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) ? path : string.Empty;
    }

    private static string SanitizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.Trim();
        sanitized = SecretAssignmentRegex().Replace(sanitized, match => $"{match.Groups[1].Value}=[redacted]");
        sanitized = BearerTokenRegex().Replace(sanitized, "$1 [redacted]");
        sanitized = LongTokenRegex().Replace(sanitized, "[redacted]");

        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    private static bool IsSensitiveKey(string key) =>
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("connection", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)\b(secret|password|pwd|token|api[_-]?key|connection[_-]?string)\s*[:=]\s*([^\s,;]+)")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)\b(authorization:\s*bearer|bearer)\s+[a-z0-9._~+/=-]{12,}")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\b[a-zA-Z0-9_=-]{36,}\b")]
    private static partial Regex LongTokenRegex();

    public sealed record RuntimeFeedbackRequest(
        string? Message,
        string? Category,
        string? EntityKey,
        string? RecordId,
        string? PagePath,
        IReadOnlyDictionary<string, object?>? Metadata);
}
