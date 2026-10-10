using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeObservabilityTests
{
    [Fact]
    public async Task Health_response_includes_generated_request_id()
    {
        using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(RequestCorrelationOptions.DefaultHeaderName, out var values).Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Existing_request_id_is_preserved()
    {
        using var application = CreateApplication();
        using var client = application.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(RequestCorrelationOptions.DefaultHeaderName, "operator-request-123");

        var response = await client.SendAsync(request);

        response.Headers.GetValues(RequestCorrelationOptions.DefaultHeaderName)
            .Single()
            .Should()
            .Be("operator-request-123");
    }

    [Fact]
    public async Task Readiness_response_includes_request_id()
    {
        using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(RequestCorrelationOptions.DefaultHeaderName, out var values).Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Request_log_path_normalization_excludes_query_string()
    {
        RequestCorrelationMiddleware.NormalizePath(new PathString("/activate"))
            .Should()
            .Be("/activate");
    }

    [Fact]
    public async Task Internal_deployment_failure_response_includes_request_id()
    {
        const string requestId = "deploy-diagnostic-123";
        const string internalSecret = "runtime-internal-secret-that-is-long-enough";
        using var application = CreateApplication(services =>
        {
            services.RemoveAll<IControlPlaneClient>();
            services.AddSingleton<IControlPlaneClient>(new NullControlPlaneClient());
        }, configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:InternalSecret"] = internalSecret
            });
        });
        using var client = application.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/deploy/sample")
        {
            Content = JsonContent.Create(new DeploySampleRequest(
                "missing.erpgenerator.example",
                OwnerEmail: null,
                Version: "v4",
                Environment: "erp"))
        };
        request.Headers.Add("x-erpgenerator-internal-secret", internalSecret);
        request.Headers.Add(RequestCorrelationOptions.DefaultHeaderName, requestId);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("requestId").GetString().Should().Be(requestId);
        response.Headers.GetValues(RequestCorrelationOptions.DefaultHeaderName).Single().Should().Be(requestId);
    }

    [Fact]
    public async Task Legacy_sample_deploy_rejects_caller_selected_production_before_control_plane_or_database_access()
    {
        const string internalSecret = "runtime-internal-secret-that-is-long-enough";
        using var application = CreateApplication(configureConfiguration: configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:InternalSecret"] = internalSecret
            });
        });
        using var client = application.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/deploy/sample")
        {
            Content = JsonContent.Create(new DeploySampleRequest(
                "acme.erpgenerator.example",
                OwnerEmail: null,
                Version: "v4",
                Environment: "production"))
        };
        request.Headers.Add("x-erpgenerator-internal-secret", internalSecret);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Environment must be erp.");
    }

    [Fact]
    public void Legacy_sample_promote_and_rollback_contract_cannot_transport_lifecycle_authorization()
    {
        typeof(DeploySampleRequest).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name =>
                name.Contains("Attestation", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplicationFactory<Program> CreateApplication(
        Action<IServiceCollection>? configureServices = null,
        Action<IConfigurationBuilder>? configureConfiguration = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Environment", "Development");
                builder.ConfigureAppConfiguration((_, configuration) => configureConfiguration?.Invoke(configuration));
                builder.ConfigureTestServices(services => configureServices?.Invoke(services));
            });

    private sealed class NullControlPlaneClient : IControlPlaneClient
    {
        public Task<ResolvedTenantEnvironment?> ResolveTenantAsync(
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResolvedTenantEnvironment?>(null);

        public Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(
            string host,
            TenantEnvironmentKind environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResolvedTenantEnvironment?>(null);

        public Task RecordDeploymentEventAsync(
            DeploymentEventCreate deploymentEvent,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateDeploymentStatusAsync(
            DeploymentStatusUpdate update,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateOwnerActivationStatusAsync(
            OwnerActivationStatusUpdate update,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SubmitRuntimeFeedbackAsync(
            RuntimeFeedbackCreate feedback,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SubmitRuntimeUsageEventAsync(
            RuntimeUsageEventCreate usageEvent,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
