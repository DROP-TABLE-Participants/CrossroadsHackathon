using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeForwardedHeadersTests
{
    [Fact]
    public void Runtime_trusts_one_network_restricted_proxy_hop_for_https_detection()
    {
        using var application = new WebApplicationFactory<Program>();

        var options = application.Services
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>()
            .Value;

        options.ForwardedHeaders.Should().Be(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.ForwardLimit.Should().Be(1);
        options.KnownIPNetworks.Should().BeEmpty();
        options.KnownProxies.Should().BeEmpty();
    }
}
