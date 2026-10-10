using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases.Scheduling;
using ErpGenerator.Runtime.WebHost.Scheduling;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeSchedulerHostedServiceTests
{
    [Fact]
    public async Task Pass_enumerates_every_page_with_bounded_parallel_failure_isolation()
    {
        var first = Tenant("first", TenantEnvironmentKind.Erp);
        var second = Tenant("second", TenantEnvironmentKind.Erp);
        var third = Tenant("third", TenantEnvironmentKind.Erp);
        var controlPlane = new PagedControlPlaneClient(
            new ControlPlaneScheduleTargetsPage(1, true, [first, second]),
            new ControlPlaneScheduleTargetsPage(2, false, [third]));
        var cycle = new CapturingCycle(second.TenantStableId);
        var service = Service(controlPlane, cycle);

        var result = await service.RunPassAsync();

        result.Should().Be(new RuntimeSchedulerPassResult(3, 2, 1, 2));
        controlPlane.Requests.Should().Equal((1, 2), (2, 2));
        cycle.Calls.Select(call => call.TenantStableId)
            .Should().BeEquivalentTo(first.TenantStableId, second.TenantStableId, third.TenantStableId);
        cycle.Calls.Should().OnlyContain(call => call.WorkerId == "hosted_test_worker");
    }

    [Fact]
    public async Task Pass_rejects_duplicate_environment_identity_across_pages()
    {
        var target = Tenant("duplicate", TenantEnvironmentKind.Erp);
        var controlPlane = new PagedControlPlaneClient(
            new ControlPlaneScheduleTargetsPage(1, true, [target]),
            new ControlPlaneScheduleTargetsPage(2, false, [target]));
        var service = Service(controlPlane, new CapturingCycle());

        var run = () => service.RunPassAsync();

        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*duplicate tenant environment*");
    }

    [Fact]
    public async Task Http_client_uses_internal_identity_and_maps_exact_paged_targets()
    {
        var handler = new StubHttpHandler(
            """
            {
              "page": 2,
              "hasNextPage": false,
              "targets": [
                {
                  "tenantId": "17",
                  "tenantStableId": "stable_17",
                  "tenantSlug": "seventeen",
                  "host": "seventeen.erpgenerator.example",
                  "environment": "erp",
                  "databaseName": "erp_tenant_stable_17_prod",
                  "databaseConnectionString": null,
                  "databaseCredentialMode": "secret_ref",
                  "databaseCredentialSecretRef": "tenant-db:stable_17:production:erp_tenant_stable_17_prod",
                  "runtimeBaseUrl": "https://seventeen.erpgenerator.example"
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler);
        var client = new HttpControlPlaneClient(
            httpClient,
            Options.Create(new ControlPlaneOptions
            {
                BaseUrl = "https://control.erpgenerator.test",
                InternalSecret = "scheduler-secret"
            }));

        var page = await client.ListScheduleTargetsAsync(2, 25);

        page.Page.Should().Be(2);
        page.HasNextPage.Should().BeFalse();
        page.Targets.Should().ContainSingle().Which.Should().Match<ResolvedTenantEnvironment>(target =>
            target.TenantStableId == "stable_17" &&
            target.Environment == TenantEnvironmentKind.Erp &&
            target.DatabaseCredentialMode == "secret_ref" &&
            target.DatabaseConnectionString == null);
        handler.Request!.RequestUri!.PathAndQuery.Should().Be(
            "/api/internal/runtime/tenants/schedule-targets?page=2&limit=25");
        handler.Request.Headers.GetValues("x-erpgenerator-internal-secret")
            .Should().Equal("scheduler-secret");
    }

    private static RuntimeSchedulerHostedService Service(
        IControlPlaneClient controlPlane,
        IRuntimeScheduleCycle cycle) => new(
        controlPlane,
        cycle,
        Options.Create(new RuntimeSchedulerHostedOptions
        {
            Enabled = true,
            PageSize = 2,
            MaxPagesPerPass = 3,
            MaxTenantConcurrency = 2,
            PollIntervalSeconds = 1,
            WorkerId = "hosted_test_worker"
        }),
        NullLogger<RuntimeSchedulerHostedService>.Instance);

    private static ResolvedTenantEnvironment Tenant(
        string key,
        TenantEnvironmentKind environment) => new(
        $"tenant-{key}",
        $"stable-{key}",
        key,
        $"{key}.erpgenerator.test",
        environment,
        $"erp_tenant_{key}",
        $"Host=localhost;Database=erp_tenant_{key}",
        $"https://{key}.erpgenerator.test",
        "legacy_connection_string");

    private sealed class CapturingCycle(params string[] failures) : IRuntimeScheduleCycle
    {
        private readonly HashSet<string> failures = failures.ToHashSet(StringComparer.Ordinal);
        private readonly List<(string TenantStableId, string WorkerId)> calls = [];

        public IReadOnlyList<(string TenantStableId, string WorkerId)> Calls
        {
            get
            {
                lock (calls)
                {
                    return [.. calls];
                }
            }
        }

        public Task<RuntimeScheduleCycleResult> RunOnceAsync(
            ResolvedTenantEnvironment tenant,
            string workerId,
            CancellationToken cancellationToken = default)
        {
            lock (calls)
            {
                calls.Add((tenant.TenantStableId, workerId));
            }

            return failures.Contains(tenant.TenantStableId)
                ? Task.FromException<RuntimeScheduleCycleResult>(new InvalidOperationException("tenant failure"))
                : Task.FromResult(new RuntimeScheduleCycleResult(true, 0, 0, 0, 0, 0, 0, 0, 0));
        }
    }

    private sealed class PagedControlPlaneClient(params ControlPlaneScheduleTargetsPage[] pages)
        : IControlPlaneClient
    {
        private readonly IReadOnlyDictionary<int, ControlPlaneScheduleTargetsPage> pages =
            pages.ToDictionary(page => page.Page);

        public List<(int Page, int Limit)> Requests { get; } = [];

        public Task<ControlPlaneScheduleTargetsPage> ListScheduleTargetsAsync(
            int page,
            int limit,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((page, limit));
            return Task.FromResult(pages[page]);
        }

        public Task<ResolvedTenantEnvironment?> ResolveTenantAsync(
            string host,
            CancellationToken cancellationToken = default) => Task.FromResult<ResolvedTenantEnvironment?>(null);

        public Task<ResolvedTenantEnvironment?> ResolveTenantEnvironmentAsync(
            string host,
            TenantEnvironmentKind environment,
            CancellationToken cancellationToken = default) => Task.FromResult<ResolvedTenantEnvironment?>(null);

        public Task RecordDeploymentEventAsync(DeploymentEventCreate deploymentEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateDeploymentStatusAsync(DeploymentStatusUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateOwnerActivationStatusAsync(OwnerActivationStatusUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubmitRuntimeFeedbackAsync(RuntimeFeedbackCreate feedback, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubmitRuntimeUsageEventAsync(RuntimeUsageEventCreate usageEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubHttpHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
