using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeTestTenant
{
    public static ResolvedTenantEnvironment Create(
        string slug,
        string host,
        string databaseName,
        TenantEnvironmentKind environment = TenantEnvironmentKind.Erp) => new(
        slug,
        $"{slug}-stable-id",
        slug,
        host,
        environment,
        databaseName,
        null,
        $"http://{host}",
        Status: "ready");
}
