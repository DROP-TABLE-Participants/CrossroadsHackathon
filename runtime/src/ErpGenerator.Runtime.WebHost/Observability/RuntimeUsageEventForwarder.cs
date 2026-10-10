using System.Text.Json;
using ErpGenerator.Runtime.Services.ControlPlane;

namespace ErpGenerator.Runtime.WebHost.Observability;

public sealed class RuntimeUsageEventForwarder(
    IControlPlaneClient controlPlane,
    ILogger<RuntimeUsageEventForwarder> logger)
{
    public async Task ForwardAsync(
        global::RuntimeRouteContext runtime,
        string eventKind,
        string? entityKey = null,
        string? recordId = null,
        string? workflowKey = null,
        string? actionKey = null,
        string? reportKey = null,
        string? pagePath = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (runtime.Tenant is null || runtime.User is null || string.IsNullOrWhiteSpace(eventKind))
        {
            return;
        }

        try
        {
            await controlPlane.SubmitRuntimeUsageEventAsync(
                new RuntimeUsageEventCreate(
                    runtime.Tenant.Host,
                    runtime.Tenant.TenantStableId,
                    runtime.Tenant.TenantSlug,
                    runtime.Tenant.Environment.ToString().ToLowerInvariant(),
                    runtime.User.UserId.ToString(),
                    runtime.User.Email,
                    runtime.User.Role,
                    SanitizeText(eventKind, 80),
                    DateTimeOffset.UtcNow,
                    SanitizeText(entityKey, 120),
                    SanitizeText(recordId, 80),
                    SanitizeText(workflowKey, 120),
                    SanitizeText(actionKey, 120),
                    SanitizeText(reportKey, 120),
                    SanitizePath(pagePath),
                    SanitizeMetadata(metadata)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Runtime usage event forwarding failed for host {Host} and event {EventKind}.",
                runtime.Tenant.Host,
                eventKind);
        }
    }

    private static IReadOnlyDictionary<string, object?> SanitizeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
        {
            return new Dictionary<string, object?>();
        }

        var safe = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, value) in metadata.Take(20))
        {
            var key = SanitizeText(rawKey, 80);
            if (string.IsNullOrWhiteSpace(key) || IsSensitiveKey(key))
            {
                continue;
            }

            safe[key] = value switch
            {
                null => null,
                string text when ContainsUnsafeValue(text) => "[redacted]",
                string text => SanitizeText(text, 240),
                bool boolean => boolean,
                int or long or decimal or double or float => value,
                JsonElement element when element.ValueKind == JsonValueKind.String =>
                    ContainsUnsafeValue(element.GetString()) ? "[redacted]" : SanitizeText(element.GetString(), 240),
                JsonElement element when element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                _ => ContainsUnsafeValue(Convert.ToString(value)) ? "[redacted]" : SanitizeText(Convert.ToString(value), 240)
            };
        }

        return safe;
    }

    private static string SanitizePath(string? value)
    {
        var path = SanitizeText(value, 240);
        return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) ? path : string.Empty;
    }

    private static string SanitizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.Trim();
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    private static bool IsSensitiveKey(string key) =>
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("company_map", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("artifact_json", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("nodes", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("edges", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsUnsafeValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("company_map", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("artifactJson", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("\"nodes\"", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("\"edges\"", StringComparison.OrdinalIgnoreCase));
}
