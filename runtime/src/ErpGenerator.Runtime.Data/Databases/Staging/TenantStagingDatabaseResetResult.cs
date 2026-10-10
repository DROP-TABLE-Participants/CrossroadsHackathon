namespace ErpGenerator.Runtime.Data.Databases.Staging;

public sealed record TenantStagingDatabaseResetResult(
    string Status,
    DateTimeOffset CompletedAt);
