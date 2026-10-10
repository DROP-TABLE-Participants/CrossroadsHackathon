using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Preparation;

public static class TenantDatabaseNamePolicy
{
    public static string BuildExpected(string tenantStableId, TenantEnvironmentKind environment)
    {
        if (environment != TenantEnvironmentKind.Erp)
            throw new ArgumentException("Environment must be erp.", nameof(environment));
        var token = new string((tenantStableId ?? string.Empty).Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant).Take(28).ToArray());
        if (token.Length == 0) throw new InvalidOperationException("Tenant stable id cannot produce a database identity token.");
        return $"erp_tenant_{token}_erp";
    }
    // Database names are immutable. Existing copies retain their exact stable-id-derived identity.
    public static bool IsExpected(string tenantStableId, string databaseName)
    {
        var expected = BuildExpected(tenantStableId, TenantEnvironmentKind.Erp);
        var prefix = expected[..^3];
        return databaseName == expected || databaseName == prefix + "staging" || databaseName == prefix + "prod";
    }
}
