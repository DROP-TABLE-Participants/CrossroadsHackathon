using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantDatabaseCredentialStoreTests
{
    [Fact]
    public void Control_plane_store_resolves_credentials_across_store_instances()
    {
        var state = new DurableCredentialState();
        var firstStore = CreateStore(state);
        var secondStore = CreateStore(state);
        var secretRef = ControlPlaneTenantDatabaseCredentialStore.BuildSecretReference(
            "tenant-stable-1",
            TenantEnvironmentKind.Staging,
            "erp_tenant_stable_1_staging");

        firstStore.Store(new TenantDatabaseCredential(
            "tenant-stable-1",
            TenantEnvironmentKind.Staging,
            "erp_tenant_stable_1_staging",
            "erp_app_tenant_staging",
            "Host=db.internal;Port=5432;Database=erp_tenant_stable_1_staging;Username=erp_app_tenant_staging;Password=fake-password",
            secretRef));

        var tenant = new ResolvedTenantEnvironment(
            "tenant-1",
            "tenant-stable-1",
            "acme",
            "acme.erpgenerator.example",
            TenantEnvironmentKind.Staging,
            "erp_tenant_stable_1_staging",
            null,
            "https://acme.erpgenerator.example",
            "secret_ref",
            secretRef);

        secondStore.TryGet(tenant, out var credential).Should().BeTrue();
        credential.SecretReference.Should().Be(secretRef);
        credential.UserName.Should().Be("erp_app_tenant_staging");
        credential.ConnectionString.Should().Contain("Host=db.internal");
        credential.ConnectionString.Should().Contain("Password=fake-password");
    }

    [Fact]
    public void Control_plane_store_errors_do_not_echo_credentials()
    {
        var state = new DurableCredentialState { ForceFailure = true };
        var store = CreateStore(state);

        var act = () => store.Store(new TenantDatabaseCredential(
            "tenant-stable-1",
            TenantEnvironmentKind.Production,
            "erp_tenant_stable_1_prod",
            "erp_app_tenant_prod",
            "Host=db.internal;Database=erp_tenant_stable_1_prod;Username=erp_app_tenant_prod;Password=fake-password"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*could not be stored*")
            .Which.Message.Should().NotContain("fake-password")
            .And.NotContain("erp_app_tenant_prod");
    }

    private static ControlPlaneTenantDatabaseCredentialStore CreateStore(DurableCredentialState state) =>
        new(
            new HttpClient(new DurableCredentialHandler(state)),
            Options.Create(new ControlPlaneOptions
            {
                BaseUrl = "https://landing.internal",
                InternalSecret = "internal-secret-value"
            }));

    private sealed class DurableCredentialState
    {
        public Dictionary<string, StoredCredential> Credentials { get; } = new(StringComparer.Ordinal);
        public bool ForceFailure { get; init; }
    }

    private sealed class DurableCredentialHandler(DurableCredentialState state) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Handle(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Handle(request));

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            if (state.ForceFailure)
            {
                return Json(HttpStatusCode.InternalServerError, new { message = "failed" });
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/api/internal/runtime/tenant-db-secrets")
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
                body.Should().NotContain("\"secretRef\"");
                body.Should().NotContain("\"connectionString\"");
                var requestPayload = JsonSerializer.Deserialize<StoreCredentialRequest>(body, JsonOptions)!;
                var secretRef = $"tenant-db:{requestPayload.TenantStableId}:{requestPayload.Environment}:{requestPayload.DatabaseName}";
                var connectionString = new NpgsqlConnectionStringBuilder
                {
                    Host = requestPayload.Host,
                    Port = requestPayload.Port,
                    Database = requestPayload.Database,
                    Username = requestPayload.Username,
                    Password = requestPayload.Password,
                    SslMode = Enum.Parse<SslMode>(requestPayload.SslMode)
                }.ConnectionString;
                var payload = new StoredCredential(
                    secretRef,
                    requestPayload.TenantStableId,
                    requestPayload.Environment,
                    requestPayload.DatabaseName,
                    requestPayload.Username,
                    connectionString);
                state.Credentials[secretRef] = payload;
                return Json(HttpStatusCode.OK, new
                {
                    payload.SecretRef,
                    payload.TenantStableId,
                    payload.Environment,
                    payload.DatabaseName,
                    payload.Username
                });
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.StartsWith("/api/internal/runtime/tenant-db-secrets/", StringComparison.Ordinal) == true)
            {
                var secretRef = Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split('/').Last());
                return state.Credentials.TryGetValue(secretRef, out var credential)
                    ? Json(HttpStatusCode.OK, credential)
                    : Json(HttpStatusCode.NotFound, new { message = "not found" });
            }

            return Json(HttpStatusCode.NotFound, new { message = "not found" });
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, object value) =>
            new(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions))
            };

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    }

    private sealed record StoredCredential(
        string SecretRef,
        string TenantStableId,
        string Environment,
        string DatabaseName,
        string Username,
        string ConnectionString);

    private sealed record StoreCredentialRequest(
        string TenantStableId,
        string Environment,
        string DatabaseName,
        string Username,
        string Host,
        int Port,
        string Database,
        string Password,
        string SslMode);
}
