using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Shared.Configuration;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.ControlPlane;

public sealed class HttpControlPlaneClient(HttpClient httpClient, IOptions<ControlPlaneOptions> options)
    : IControlPlaneClient
{
    private readonly ControlPlaneOptions options = options.Value;

    public async Task<ResolvedTenantEnvironment?> ResolveTenantAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        return await ResolveTenantAsync(host, environment: null, cancellationToken);
    }

    public async Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(
        string host,
        TenantEnvironmentKind environment,
        CancellationToken cancellationToken = default)
    {
        return await ResolveTenantAsync(host, environment.ToString().ToLowerInvariant(), cancellationToken);
    }

    public async Task<ControlPlaneScheduleTargetsPage> ListScheduleTargetsAsync(
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return new ControlPlaneScheduleTargetsPage(page, false, []);
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            $"/api/internal/runtime/tenants/schedule-targets?page={page}&limit={limit}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ControlPlaneScheduleTargetsResponse>(cancellationToken)
                      ?? throw new InvalidOperationException(
                          "Control-plane schedule target response was empty.");
        if (payload.Page != page || payload.Targets is null)
        {
            throw new InvalidOperationException(
                "Control-plane schedule target pagination did not match the exact request.");
        }

        var targets = payload.Targets.Select(ToResolvedTenantEnvironment).ToArray();
        if (targets.Select(target => $"{target.TenantStableId}:{target.Environment}")
            .Distinct(StringComparer.Ordinal)
            .Count() != targets.Length)
        {
            throw new InvalidOperationException(
                "Control-plane schedule target page contains duplicate tenant environments.");
        }

        return new ControlPlaneScheduleTargetsPage(payload.Page, payload.HasNextPage, targets);
    }

    private async Task<ResolvedTenantEnvironment?> ResolveTenantAsync(
        string host,
        string? environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return null;
        }

        var path = $"/api/internal/runtime/tenants/resolve?host={Uri.EscapeDataString(host)}";
        if (!string.IsNullOrWhiteSpace(environment))
        {
            path += $"&environment={Uri.EscapeDataString(environment)}";
        }

        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ControlPlaneResolveResponse>(cancellationToken);
        if (payload is null)
        {
            return null;
        }

        return ToResolvedTenantEnvironment(payload);
    }

    public async Task RecordDeploymentEventAsync(
        DeploymentEventCreate deploymentEvent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return;
        }

        using var request = CreateRequest(HttpMethod.Post, "/api/internal/runtime/deployment-events");
        request.Content = JsonContent.Create(deploymentEvent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateDeploymentStatusAsync(
        DeploymentStatusUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return;
        }

        using var request = CreateRequest(HttpMethod.Post, "/api/internal/runtime/deployments/status");
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateOwnerActivationStatusAsync(
        OwnerActivationStatusUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return;
        }

        using var request = CreateRequest(HttpMethod.Post, "/api/internal/runtime/owner-activations/status");
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SubmitRuntimeFeedbackAsync(
        RuntimeFeedbackCreate feedback,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return;
        }

        using var request = CreateRequest(HttpMethod.Post, "/api/internal/runtime/feedback");
        request.Content = JsonContent.Create(feedback);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SubmitRuntimeUsageEventAsync(
        RuntimeUsageEventCreate usageEvent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return;
        }

        using var request = CreateRequest(HttpMethod.Post, "/api/internal/runtime/usage-events");
        request.Content = JsonContent.Create(usageEvent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var baseUri = new Uri(options.BaseUrl.TrimEnd('/'));
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        if (!string.IsNullOrWhiteSpace(options.InternalSecret))
        {
            request.Headers.Add("x-erpgenerator-internal-secret", options.InternalSecret);
        }

        return request;
    }

    private static TenantEnvironmentKind ParseEnvironment(string value) =>
        value.Equals("production", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("prod", StringComparison.OrdinalIgnoreCase)
            ? TenantEnvironmentKind.Production
            : TenantEnvironmentKind.Staging;

    private static ResolvedTenantEnvironment ToResolvedTenantEnvironment(
        ControlPlaneResolveResponse payload)
    {
        var credentialMode = string.IsNullOrWhiteSpace(payload.DatabaseCredentialMode)
            ? !string.IsNullOrWhiteSpace(payload.DatabaseConnectionString)
                ? "legacy_connection_string"
                : "secret_ref"
            : payload.DatabaseCredentialMode;
        if (string.IsNullOrWhiteSpace(payload.TenantId) ||
            string.IsNullOrWhiteSpace(payload.TenantStableId) ||
            string.IsNullOrWhiteSpace(payload.TenantSlug) ||
            string.IsNullOrWhiteSpace(payload.Host) ||
            string.IsNullOrWhiteSpace(payload.DatabaseName) ||
            string.IsNullOrWhiteSpace(payload.RuntimeBaseUrl) ||
            payload.Environment is not ("staging" or "production") ||
            credentialMode is not ("legacy_connection_string" or "secret_ref") ||
            credentialMode == "legacy_connection_string" &&
            string.IsNullOrWhiteSpace(payload.DatabaseConnectionString) ||
            credentialMode == "secret_ref" &&
            string.IsNullOrWhiteSpace(payload.DatabaseCredentialSecretRef))
        {
            throw new InvalidOperationException(
                "Control-plane tenant environment response is incomplete or unsupported.");
        }

        return new ResolvedTenantEnvironment(
            payload.TenantId,
            payload.TenantStableId,
            payload.TenantSlug,
            payload.Host,
            ParseEnvironment(payload.Environment),
            payload.DatabaseName,
            payload.DatabaseConnectionString,
            payload.RuntimeBaseUrl,
            credentialMode,
            payload.DatabaseCredentialSecretRef);
    }

    private sealed record ControlPlaneResolveResponse(
        string TenantId,
        string TenantStableId,
        string TenantSlug,
        string Host,
        string Environment,
        string DatabaseName,
        string? DatabaseConnectionString,
        string RuntimeBaseUrl,
        string? DatabaseCredentialMode = null,
        string? DatabaseCredentialSecretRef = null);

    private sealed record ControlPlaneScheduleTargetsResponse(
        int Page,
        bool HasNextPage,
        IReadOnlyList<ControlPlaneResolveResponse> Targets);
}
