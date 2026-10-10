using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeDeploymentBrokerAuthorizationTests
{
    private const string BrokerSecret = "agent-runtime-broker-secret-12345";
    private const string RuntimeControlPlaneSecret = "api-runtime-secret-123456789012";
    private const string PortalGatewaySecret = "portal-gateway-secret-123456789";
    private const string OperationId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData(null, null)]
    [InlineData("x-erpgenerator-internal-secret", BrokerSecret)]
    [InlineData("x-erpgenerator-deployment-broker-secret", RuntimeControlPlaneSecret)]
    [InlineData("x-erpgenerator-deployment-broker-secret", PortalGatewaySecret)]
    [InlineData("x-erpgenerator-deployment-broker-secret", "wrong-secret")]
    public async Task Broker_rejects_missing_wrong_and_cross_domain_secrets(string? headerName, string? value)
    {
        var broker = new CapturingBroker();
        using var application = CreateApplication(broker);
        using var client = application.CreateClient();
        using var request = ResetRequest();
        if (headerName is not null)
        {
            request.Headers.Add(headerName, value);
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        broker.ResetRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Broker_accepts_only_dedicated_secret_and_exact_idempotency_key()
    {
        var broker = new CapturingBroker();
        using var application = CreateApplication(broker);
        using var client = application.CreateClient();
        using var request = ResetRequest();
        request.Headers.Add(HttpRuntimeHeader(), BrokerSecret);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        broker.ResetRequests.Should().ContainSingle();
        broker.ResetRequests[0].OperationId.Should().Be(OperationId);
        broker.ResetRequests[0].Target.Should().Be(Target());
    }

    [Fact]
    public async Task Broker_rejects_mismatched_idempotency_header_before_service_execution()
    {
        var broker = new CapturingBroker();
        using var application = CreateApplication(broker);
        using var client = application.CreateClient();
        using var request = ResetRequest(idempotencyKey: "different-operation");
        request.Headers.Add(HttpRuntimeHeader(), BrokerSecret);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        broker.ResetRequests.Should().BeEmpty();
    }

    [Fact]
    public void Broker_authorization_fails_closed_and_does_not_accept_runtime_secret()
    {
        RuntimeDeploymentBrokerAuth.IsAuthorized(BrokerSecret, BrokerSecret).Should().BeTrue();
        RuntimeDeploymentBrokerAuth.IsAuthorized(RuntimeControlPlaneSecret, BrokerSecret).Should().BeFalse();
        RuntimeDeploymentBrokerAuth.IsAuthorized(PortalGatewaySecret, BrokerSecret).Should().BeFalse();
        RuntimeDeploymentBrokerAuth.IsAuthorized(null, BrokerSecret).Should().BeFalse();
        RuntimeDeploymentBrokerAuth.IsAuthorized(BrokerSecret, null).Should().BeFalse();
    }

    private static WebApplicationFactory<Program> CreateApplication(CapturingBroker broker) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Environment", "Development");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["RuntimeDeploymentBroker:Secret"] = BrokerSecret,
                        ["Runtime:InternalSecret"] = RuntimeControlPlaneSecret
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IRuntimeDeploymentBrokerService>();
                    services.AddSingleton<IRuntimeDeploymentBrokerService>(broker);
                });
            });

    private static HttpRequestMessage ResetRequest(string idempotencyKey = OperationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/deployment-broker/staging/reset")
        {
            Content = JsonContent.Create(new RuntimeStagingResetBrokerRequest(
                OperationId,
                "run-1",
                "project-1",
                Target()))
        };
        request.Headers.Add(DeploymentBrokerRoutes.IdempotencyHeaderName, idempotencyKey);
        return request;
    }

    private static RuntimeDeploymentTarget Target() =>
        new(
            "tenant-1",
            "stable-tenant-1",
            "acme-staging.erpgenerator.example",
            "staging",
            "erp_tenant_stabletenant1_staging");

    private static string HttpRuntimeHeader() => RuntimeDeploymentBrokerAuth.HeaderName;

    private sealed class CapturingBroker : IRuntimeDeploymentBrokerService
    {
        public List<RuntimeStagingResetBrokerRequest> ResetRequests { get; } = [];

        public Task<RuntimeStagingResetBrokerResponse> ResetStagingAsync(RuntimeStagingResetBrokerRequest request, CancellationToken cancellationToken = default)
        {
            ResetRequests.Add(request);
            return Task.FromResult(new RuntimeStagingResetBrokerResponse(
                true,
                "reset",
                request.OperationId,
                request.Target,
                "https://acme-staging.erpgenerator.example"));
        }

        public Task<RuntimeStagingDeploymentBrokerResponse> DeployStagingAsync(RuntimeStagingDeploymentBrokerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RuntimeProductionPromotionBrokerResponse> PromoteProductionAsync(RuntimeProductionPromotionBrokerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RuntimeOwnerActivationBrokerResponse> CreateOwnerActivationAsync(RuntimeOwnerActivationBrokerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
