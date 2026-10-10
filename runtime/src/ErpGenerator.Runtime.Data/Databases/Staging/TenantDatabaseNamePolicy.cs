using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Staging;

public static class TenantDatabaseNamePolicy
{
    public static string BuildExpected(string tenantStableId, TenantEnvironmentKind environment)
    {
        var token = new string((tenantStableId ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .Take(28)
            .ToArray());
        if (token.Length == 0)
        {
            throw new InvalidOperationException("Tenant stable id cannot produce a database identity token.");
        }

        var environmentToken = environment == TenantEnvironmentKind.Production ? "prod" : "staging";
        var name = $"erp_tenant_{token}_{environmentToken}";
        return name.Length <= 63 ? name : name[..63];
    }
}
