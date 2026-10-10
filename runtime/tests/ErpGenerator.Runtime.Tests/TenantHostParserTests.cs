using FluentAssertions;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantHostParserTests
{
    [Theory]
    [InlineData("acme.erpgenerator.example", "acme")]
    [InlineData("acme.erpgenerator.example:443", "acme")]
    [InlineData("staging.acme.erpgenerator.example", "staging")]
    public void It_derives_the_leftmost_subdomain_from_registered_runtime_hosts(string host, string expected)
    {
        var parser = new TenantHostParser();

        parser.TryGetSubdomain(host, "erpgenerator.example").Should().Be(expected);
    }

    [Theory]
    [InlineData("erpgenerator.example")]
    [InlineData("www.erpgenerator.example")]
    [InlineData("localhost:5000")]
    public void It_does_not_treat_root_or_local_hosts_as_tenants(string host)
    {
        var parser = new TenantHostParser();

        parser.TryGetSubdomain(host, "erpgenerator.example").Should().BeNull();
    }
}
