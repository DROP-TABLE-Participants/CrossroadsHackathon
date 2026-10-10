using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeSessionServiceTests
{
    [Fact]
    public void Session_is_bound_to_tenant_stable_id_and_host()
    {
        var service = CreateService();
        var token = service.CreateSession("tenant-stable-id", "acme.erpgenerator.example", 42, "owner@acme.test", "owner");

        service.ValidateSession(token, "tenant-stable-id", "acme.erpgenerator.example").Should().NotBeNull();
        service.ValidateSession(token, "tenant-stable-id", "other.erpgenerator.example").Should().BeNull();
        service.ValidateSession(token, "other-stable-id", "acme.erpgenerator.example").Should().BeNull();
    }

    [Fact]
    public void Portal_or_malformed_tokens_do_not_authenticate_runtime()
    {
        var service = CreateService();

        service.ValidateSession("not-a-runtime-token", "tenant-stable-id", "acme.erpgenerator.example").Should().BeNull();
        service.ValidateSession("payload.signature.extra", "tenant-stable-id", "acme.erpgenerator.example").Should().BeNull();
    }

    private static RuntimeSessionService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:SessionSecret"] = "test-runtime-session-secret"
            })
            .Build();

        return new RuntimeSessionService(configuration);
    }
}
