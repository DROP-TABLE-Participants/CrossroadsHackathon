namespace ErpGenerator.Runtime.Data.Databases.Preparation;

public sealed record TenantPreparationDatabaseResetResult(
    string Status,
    DateTimeOffset CompletedAt);
