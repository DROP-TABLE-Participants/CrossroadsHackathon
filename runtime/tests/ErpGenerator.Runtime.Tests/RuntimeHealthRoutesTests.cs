using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeHealthRoutesTests
{
    [Fact]
    public async Task Readiness_endpoint_is_tenant_agnostic()
    {
        using var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Environment", "Development");
            });

        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://unknown.erpgenerator.test")
        });

        var response = await client.GetAsync("/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"ready\"");
        body.Should().Contain("\"service\":\"erpgenerator-runtime\"");
    }
}
