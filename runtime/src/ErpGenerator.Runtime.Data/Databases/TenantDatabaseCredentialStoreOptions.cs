namespace ErpGenerator.Runtime.Data.Databases;

public sealed class TenantDatabaseCredentialStoreOptions
{
    public TenantDatabaseCredentialStoreMode Mode { get; init; } = TenantDatabaseCredentialStoreMode.InMemory;
}
