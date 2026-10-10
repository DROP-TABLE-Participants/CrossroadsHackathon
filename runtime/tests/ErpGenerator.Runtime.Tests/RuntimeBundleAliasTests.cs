using FluentAssertions;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeBundleAliasTests
{
    [Theory]
    [InlineData("manufacturing", "manufacturing/v1")]
    [InlineData("manufacturing/v1", "manufacturing/v1")]
    [InlineData("manufacturing-demo", "manufacturing/v1")]
    [InlineData("it-services", "it-services/v4")]
    [InlineData("it-services/v4", "it-services/v4")]
    public void Sample_deploy_bundle_aliases_resolve_to_expected_bundle_paths(string alias, string expectedSuffix)
    {
        var resolved = RuntimeRouteHelpers.ResolveSampleBundlePath("/repo/src/ErpGenerator.Runtime.WebHost", alias);

        resolved.Replace('\\', '/').Should().EndWith($"/bundles/{expectedSuffix}");
    }
}
