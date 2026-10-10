using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

internal static class TenantDatabaseBackupStorageKeys
{
    public static string BuildObjectKey(
        string prefix,
        ResolvedTenantEnvironment tenant,
        string backupId,
        DateTimeOffset createdAt)
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        var environment = tenant.Environment.ToString().ToLowerInvariant();
        return string.Concat(
            normalizedPrefix,
            SanitizePart(tenant.TenantStableId),
            "/",
            environment,
            "/",
            SanitizePart(tenant.DatabaseName),
            "/",
            createdAt.ToString("yyyyMMddHHmmss"),
            "_",
            SanitizePart(backupId),
            ".dump");
    }

    public static string SanitizePart(string value)
    {
        var safe = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "backup" : safe;
    }

    private static string NormalizePrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized + "/";
    }
}
