namespace ErpGenerator.Runtime.Shared.Compilation;

/// <summary>
/// Runtime-owned role ids that are never part of an ERP Bundle's business RBAC model.
/// </summary>
public static class RuntimeSystemRoleIds
{
    public const string TenantOwner = "_runtime_owner";

    public static bool IsReserved(string? roleId) =>
        string.Equals(roleId?.Trim(), TenantOwner, StringComparison.OrdinalIgnoreCase);
}
