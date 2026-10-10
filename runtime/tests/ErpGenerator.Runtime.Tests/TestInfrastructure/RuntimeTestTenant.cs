using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeTestTenant
{
    public static ResolvedTenantEnvironment Create(
        string slug,
        string host,
        string databaseName,
        TenantEnvironmentKind environment = TenantEnvironmentKind.Staging) => new(
        slug,
        $"{slug}-stable-id",
        slug,
        host,
        environment,
        databaseName,
        null,
        $"http://{host}");
}
