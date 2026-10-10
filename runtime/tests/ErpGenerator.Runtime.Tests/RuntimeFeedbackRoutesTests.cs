using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeFeedbackRoutesTests
{
    [Fact]
    public async Task Feedback_endpoint_requires_runtime_authentication()
    {
        var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", "erp_tenant_feedback_auth");
        using var application = CreateApplication(tenant, new CapturingFeedbackControlPlaneClient(tenant), "feedback-auth-secret", null);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime/feedback");
        request.Headers.Host = tenant.Host;
        request.Content = JsonContent.Create(new { message = "Missing invoice field." });

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task Feedback_endpoint_rejects_missing_message()
    {
        await WithRuntimeAsync(async context =>
        {
            using var request = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Post, "/api/runtime/feedback", context.Tenant.Host, context.Cookie);
            request.Content = JsonContent.Create(new { category = "general" });

            var response = await context.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            body.Should().Contain("Feedback message is required.");
            context.ControlPlane.SubmittedFeedback.Should().BeNull();
        });
    }

    [PostgresFact]
    public async Task Feedback_endpoint_forwards_safe_payload_and_writes_audit_event()
    {
        await WithRuntimeAsync(async context =>
        {
            using var request = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Post, "/api/runtime/feedback", context.Tenant.Host, context.Cookie);
            request.Content = JsonContent.Create(new
            {
                message = "Finance keeps adding a missing billing entity field to invoices. token=abc123456789012345678901234567890123456",
                category = "missing_field",
                entityKey = "invoices",
                recordId = "42",
                pagePath = "/entities/invoices/42",
                metadata = new Dictionary<string, object?>
                {
                    ["screen"] = "detail",
                    ["password"] = "do-not-forward"
                }
            });

            var response = await context.Client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            context.ControlPlane.SubmittedFeedback.Should().NotBeNull();
            context.ControlPlane.SubmittedFeedback!.Message.Should().Contain("billing entity field");
            context.ControlPlane.SubmittedFeedback.Message.Should().Contain("[redacted]");
            context.ControlPlane.SubmittedFeedback.Metadata.Should().ContainKey("screen");
            context.ControlPlane.SubmittedFeedback.Metadata.Should().NotContainKey("password");
            context.ControlPlane.SubmittedUsageEvents.Should().ContainSingle(usage =>
                usage.EventKind == "feedback_submitted" &&
                usage.EntityKey == "invoices" &&
                usage.RecordId == "42" &&
                usage.Metadata!.ContainsKey("category"));
            JsonSerializer.Serialize(context.ControlPlane.SubmittedUsageEvents).Should().NotContain("billing entity field");
            JsonSerializer.Serialize(context.ControlPlane.SubmittedUsageEvents).Should().NotContain("do-not-forward");
            context.ControlPlane.SubmittedFeedback.Should().Match<RuntimeFeedbackCreate>(feedback =>
                feedback.Host == context.Tenant.Host &&
                feedback.TenantStableId == context.Tenant.TenantStableId &&
                feedback.Environment == "staging" &&
                feedback.RuntimeUserEmail == context.Owner.Email &&
                feedback.EntityKey == "invoices" &&
                feedback.RecordId == "42" &&
                feedback.PagePath == "/entities/invoices/42");

            var events = await context.Records.ListAuditEventsAsync(context.Tenant, 20);
            events.Should().ContainSingle(evt => evt.EventType == "feedback.submitted");
            var audit = events.Single(evt => evt.EventType == "feedback.submitted");
            audit.Metadata.GetProperty("messageLength").GetInt32().Should().BeGreaterThan(20);
            audit.Metadata.GetRawText().Should().NotContain("billing entity field");
            audit.Metadata.GetRawText().Should().NotContain("do-not-forward");
        });
    }

    [PostgresFact]
    public async Task Feedback_endpoint_succeeds_when_usage_event_forwarding_fails()
    {
        await WithRuntimeAsync(async context =>
        {
            context.ControlPlane.FailUsageWith = new InvalidOperationException("Password=super-secret");

            using var request = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Post, "/api/runtime/feedback", context.Tenant.Host, context.Cookie);
            request.Content = JsonContent.Create(new
            {
                message = "Please add a billing entity field.",
                category = "missing_field"
            });

            var response = await context.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.Should().NotContain("super-secret");
            context.ControlPlane.SubmittedFeedback.Should().NotBeNull();
        });
    }

    [PostgresFact]
    public async Task Feedback_endpoint_returns_sanitized_error_when_forwarding_fails()
    {
        await WithRuntimeAsync(async context =>
        {
            context.ControlPlane.FailWith = new InvalidOperationException("connection string Password=super-secret failed");

            using var request = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Post, "/api/runtime/feedback", context.Tenant.Host, context.Cookie);
            request.Content = JsonContent.Create(new
            {
                message = "Please add a field.",
                category = "missing_field"
            });

            var response = await context.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            body.Should().Contain("Feedback could not be submitted right now.");
            body.Should().NotContain("super-secret");
            body.Should().NotContain("Password=");
        });
    }

    private static async Task WithRuntimeAsync(Func<RuntimeFeedbackTestContext, Task> run)
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_feedback_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, tenant, $"owner-{suffix}@acme.test");

            var sessionSecret = $"feedback-test-{suffix}";
            var controlPlane = new CapturingFeedbackControlPlaneClient(tenant);
            using var application = CreateApplication(tenant, controlPlane, sessionSecret, adminConnectionString);
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var cookie = RuntimeHttpTestHelper.CreateSessionCookie(sessionSecret, tenant.TenantStableId, tenant.Host, owner.UserId, owner.Email, owner.Role);

            await run(new RuntimeFeedbackTestContext(tenant, owner, records, client, cookie, controlPlane));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static WebApplicationFactory<Program> CreateApplication(
        ResolvedTenantEnvironment tenant,
        CapturingFeedbackControlPlaneClient controlPlane,
        string sessionSecret,
        string? tenantConnectionStringTemplate) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Runtime:SessionSecret"] = sessionSecret,
                        ["RuntimeDatabase:TenantConnectionStringTemplate"] = tenantConnectionStringTemplate
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IControlPlaneClient>();
                    services.AddSingleton<IControlPlaneClient>(controlPlane);
                });
            });

    private sealed record RuntimeFeedbackTestContext(
        ResolvedTenantEnvironment Tenant,
        RuntimeUserContext Owner,
        RuntimeRecordStore Records,
        HttpClient Client,
        string Cookie,
        CapturingFeedbackControlPlaneClient ControlPlane);

    private sealed class CapturingFeedbackControlPlaneClient(ResolvedTenantEnvironment tenant) : IControlPlaneClient
    {
        public RuntimeFeedbackCreate? SubmittedFeedback { get; private set; }

        public List<RuntimeUsageEventCreate> SubmittedUsageEvents { get; } = [];

        public Exception? FailWith { get; set; }

        public Exception? FailUsageWith { get; set; }

        public Task<ResolvedTenantEnvironment?> ResolveTenantAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase) ? tenant : null);

        public Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(string host, TenantEnvironmentKind environment, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(host, tenant.Host, StringComparison.OrdinalIgnoreCase) && tenant.Environment == environment ? tenant : null);

        public Task RecordDeploymentEventAsync(DeploymentEventCreate deploymentEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateDeploymentStatusAsync(DeploymentStatusUpdate update, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateOwnerActivationStatusAsync(OwnerActivationStatusUpdate update, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SubmitRuntimeFeedbackAsync(RuntimeFeedbackCreate feedback, CancellationToken cancellationToken = default)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            SubmittedFeedback = feedback;
            return Task.CompletedTask;
        }

        public Task SubmitRuntimeUsageEventAsync(RuntimeUsageEventCreate usageEvent, CancellationToken cancellationToken = default)
        {
            if (FailUsageWith is not null)
            {
                throw FailUsageWith;
            }

            SubmittedUsageEvents.Add(usageEvent);
            return Task.CompletedTask;
        }
    }
}
