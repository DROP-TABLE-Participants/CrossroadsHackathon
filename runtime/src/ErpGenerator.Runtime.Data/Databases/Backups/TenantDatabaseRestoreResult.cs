namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed record TenantDatabaseRestoreResult(
    bool Success,
    string Status,
    string RestoreDatabaseName,
    string? ErrorMessage);
