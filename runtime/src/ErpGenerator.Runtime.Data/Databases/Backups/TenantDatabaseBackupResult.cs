namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed record TenantDatabaseBackupResult(
    bool Success,
    bool Required,
    string Status,
    TenantDatabaseBackupMetadata Metadata)
{
    public bool BlocksPromotion => Required && !Success;
}
