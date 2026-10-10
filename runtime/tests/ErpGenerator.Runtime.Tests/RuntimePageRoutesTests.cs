using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimePageRoutesTests
{
    [Fact]
    public async Task Embedded_workflow_history_skips_an_instance_revoked_after_the_list_snapshot()
    {
        var instances = new[] { Instance(1), Instance(2), Instance(3) };
        var loaded = new List<long>();

        var details = await PageRoutes.LoadEmbeddedWorkflowDetailsAsync(
            instances,
            (instanceId, _) =>
            {
                loaded.Add(instanceId);
                return instanceId == 2
                    ? Task.FromException<RuntimeWorkflowInstanceDetail?>(
                        new RuntimeWorkflowPermissionException("revoked after list"))
                    : Task.FromResult<RuntimeWorkflowInstanceDetail?>(
                        new RuntimeWorkflowInstanceDetail(
                            instances.Single(instance => instance.Id == instanceId),
                            [],
                            []));
            },
            CancellationToken.None);

        loaded.Should().Equal(1, 2, 3);
        details.Select(detail => detail.Instance.Id).Should().Equal(1, 3);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    public async Task Html_pages_return_404_for_unknown_tenant_hosts(string path)
    {
        using var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IControlPlaneClient>();
                    services.AddSingleton<IControlPlaneClient>(new MultiTenantControlPlaneClient());
                });
            });
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "unknown.erpgenerator.example";

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Tenant not found");
    }

    private static RuntimeWorkflowInstance Instance(long id) => new(
        id,
        "approval",
        "Approval",
        "request",
        "request",
        42,
        "waiting",
        "approval",
        7,
        null,
        JsonSerializer.SerializeToElement(new { }),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
