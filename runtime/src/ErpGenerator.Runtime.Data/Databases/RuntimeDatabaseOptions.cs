namespace ErpGenerator.Runtime.Data.Databases;

public sealed class RuntimeDatabaseOptions
{
    public string AdminConnectionString { get; init; } = string.Empty;

    public string TenantConnectionStringTemplate { get; init; } = string.Empty;

    public bool AutoProvisionDatabases { get; init; }

    public TenantDatabaseCredentialMode CredentialMode { get; init; } = TenantDatabaseCredentialMode.Template;

    public bool ProvisionTenantAppUsers { get; init; }

    public string TenantAppUserPrefix { get; init; } = "erp_app";
}
