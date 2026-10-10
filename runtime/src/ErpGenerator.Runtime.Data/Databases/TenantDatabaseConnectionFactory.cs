using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed class TenantDatabaseConnectionFactory(IOptions<RuntimeDatabaseOptions> options)
    : ITenantDatabaseConnectionFactory
{
    private readonly RuntimeDatabaseOptions options = options.Value;
    private readonly ITenantDatabaseCredentialStore? credentialStore;

    public TenantDatabaseConnectionFactory(
        IOptions<RuntimeDatabaseOptions> options,
        ITenantDatabaseCredentialStore credentialStore)
        : this(options)
    {
        this.credentialStore = credentialStore;
    }

    public string CreateConnectionString(ResolvedTenantEnvironment tenant) =>
        CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Runtime);

    public string CreateConnectionString(ResolvedTenantEnvironment tenant, TenantDatabaseConnectionPurpose purpose)
    {
        return purpose switch
        {
            TenantDatabaseConnectionPurpose.Runtime => CreateRuntimeConnectionString(tenant),
            TenantDatabaseConnectionPurpose.Provisioning => CreateProvisioningConnectionString(tenant),
            TenantDatabaseConnectionPurpose.Scheduler => CreateSchedulerConnectionString(tenant),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unsupported tenant database connection purpose.")
        };
    }

    private string CreateSchedulerConnectionString(ResolvedTenantEnvironment tenant)
    {
        // Scheduler passes enumerate every tenant environment. A normal Npgsql
        // pool is keyed by the complete connection string, including database,
        // and would therefore retain at least one physical connection per
        // tenant. Derive from the tenant-local runtime credential (never the
        // provisioning admin) and keep ownership bounded by closing each
        // connection at the end of its cycle instead of accumulating pools.
        var builder = new NpgsqlConnectionStringBuilder(CreateRuntimeConnectionString(tenant))
        {
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private string CreateRuntimeConnectionString(ResolvedTenantEnvironment tenant)
    {
        if (credentialStore is not null && credentialStore.TryGet(tenant, out var credential))
        {
            return PostgresConnectionString.Normalize(credential.ConnectionString);
        }

        if (options.CredentialMode == TenantDatabaseCredentialMode.Provisioned)
        {
            throw new InvalidOperationException($"Runtime tenant database credential is not available for database '{tenant.DatabaseName}'.");
        }

        return CreateTemplateConnectionString(tenant);
    }

    private string CreateProvisioningConnectionString(ResolvedTenantEnvironment tenant)
    {
        if (!string.IsNullOrWhiteSpace(options.AdminConnectionString))
        {
            return PostgresConnectionString.Normalize(options.AdminConnectionString, tenant.DatabaseName);
        }

        return CreateTemplateConnectionString(tenant);
    }

    private string CreateTemplateConnectionString(ResolvedTenantEnvironment tenant)
    {
        if (!string.IsNullOrWhiteSpace(tenant.DatabaseConnectionString))
        {
            return PostgresConnectionString.Normalize(tenant.DatabaseConnectionString);
        }

        if (string.IsNullOrWhiteSpace(options.TenantConnectionStringTemplate))
        {
            throw new InvalidOperationException("Runtime tenant connection string template is not configured.");
        }

        return PostgresConnectionString.Normalize(options.TenantConnectionStringTemplate, tenant.DatabaseName);
    }
}
