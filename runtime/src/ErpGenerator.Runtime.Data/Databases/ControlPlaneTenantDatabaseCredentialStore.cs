using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Shared.Configuration;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed class ControlPlaneTenantDatabaseCredentialStore(
    HttpClient httpClient,
    IOptions<ControlPlaneOptions> options)
    : ITenantDatabaseCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ControlPlaneOptions options = options.Value;

    public void Store(TenantDatabaseCredential credential)
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(credential.ConnectionString));
        var requestBody = new StoreTenantDatabaseCredentialRequest(
            credential.TenantStableId,
            ToControlPlaneEnvironment(credential.Environment),
            credential.DatabaseName,
            credential.UserName,
            builder.Host ?? string.Empty,
            builder.Port,
            builder.Database ?? credential.DatabaseName,
            builder.Password ?? string.Empty,
            builder.SslMode.ToString());

        using var request = CreateRequest(HttpMethod.Post, "/api/internal/runtime/tenant-db-secrets");
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);
        using var response = Send(request, "store");

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Tenant database credential could not be stored in the control plane. Status={(int)response.StatusCode}.");
        }
    }

    public bool TryGet(ResolvedTenantEnvironment tenant, out TenantDatabaseCredential credential)
    {
        var secretReference = string.IsNullOrWhiteSpace(tenant.DatabaseCredentialSecretRef)
            ? BuildSecretReference(tenant.TenantStableId, tenant.Environment, tenant.DatabaseName)
            : tenant.DatabaseCredentialSecretRef.Trim();
        using var request = CreateRequest(
            HttpMethod.Get,
            $"/api/internal/runtime/tenant-db-secrets/{Uri.EscapeDataString(secretReference)}");
        using var response = Send(request, "resolve");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            credential = null!;
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Tenant database credential could not be resolved from the control plane. Status={(int)response.StatusCode}.");
        }

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var payload = JsonSerializer.Deserialize<ResolveTenantDatabaseCredentialResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Tenant database credential response was empty.");

        credential = new TenantDatabaseCredential(
            payload.TenantStableId,
            ParseEnvironment(payload.Environment),
            payload.DatabaseName,
            payload.Username,
            PostgresConnectionString.Normalize(payload.ConnectionString),
            payload.SecretRef);
        return true;
    }

    public static string BuildSecretReference(
        string tenantStableId,
        TenantEnvironmentKind environment,
        string databaseName) =>
        $"tenant-db:{tenantStableId}:{ToControlPlaneEnvironment(environment)}:{databaseName}";

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("Control-plane base URL is required for tenant database credential storage.");
        }

        var request = new HttpRequestMessage(method, new Uri(new Uri(options.BaseUrl.TrimEnd('/')), path));
        if (!string.IsNullOrWhiteSpace(options.InternalSecret))
        {
            request.Headers.Add("x-erpgenerator-internal-secret", options.InternalSecret);
        }

        return request;
    }

    private HttpResponseMessage Send(HttpRequestMessage request, string action)
    {
        try
        {
            return httpClient.Send(request);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Tenant database credential control-plane {action} request failed.",
                exception);
        }
    }

    private static string ToControlPlaneEnvironment(TenantEnvironmentKind environment) =>
        environment == TenantEnvironmentKind.Production ? "production" : "staging";

    private static TenantEnvironmentKind ParseEnvironment(string value) =>
        value.Equals("production", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("prod", StringComparison.OrdinalIgnoreCase)
            ? TenantEnvironmentKind.Production
            : TenantEnvironmentKind.Staging;

    private sealed record StoreTenantDatabaseCredentialRequest(
        string TenantStableId,
        string Environment,
        string DatabaseName,
        string Username,
        string Host,
        int Port,
        string Database,
        string Password,
        string SslMode);

    private sealed record ResolveTenantDatabaseCredentialResponse(
        string SecretRef,
        string TenantStableId,
        string Environment,
        string DatabaseName,
        string Username,
        string ConnectionString);
}
