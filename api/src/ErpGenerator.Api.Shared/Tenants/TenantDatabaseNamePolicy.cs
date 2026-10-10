namespace ErpGenerator.Api.Shared.Tenants;

public static class TenantDatabaseNamePolicy
{
    public static string BuildExpected(string tenantStableId, string environment)
    {
        if (!environment.Equals("erp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Environment must be erp.", nameof(environment));
        var token = new string((tenantStableId ?? string.Empty).Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant).Take(28).ToArray());
        if (token.Length == 0) throw new InvalidOperationException("Tenant stable id cannot produce a database identity token.");
        return $"erp_tenant_{token}_erp";
    }
}
