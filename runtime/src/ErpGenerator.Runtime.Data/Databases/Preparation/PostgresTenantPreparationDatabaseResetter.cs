using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Preparation;

public sealed class PostgresTenantPreparationDatabaseResetter : ITenantPreparationDatabaseResetter
{
    private readonly RuntimeDatabaseOptions options;
    private readonly ITenantDatabaseProvisioner provisioner;
    private readonly ITenantDatabaseCredentialStore credentialStore;
    private readonly ITenantPreparationDatabaseResetExecution execution;
    private readonly ITenantPreparationOperationFence fence;

    public PostgresTenantPreparationDatabaseResetter(
        IOptions<RuntimeDatabaseOptions> options,
        ITenantDatabaseProvisioner provisioner,
        ITenantDatabaseCredentialStore credentialStore,
        ITenantPreparationOperationFence fence)
        : this(options, provisioner, credentialStore, fence, new PostgresTenantPreparationDatabaseResetExecution())
    {
    }

    internal PostgresTenantPreparationDatabaseResetter(
        IOptions<RuntimeDatabaseOptions> options,
        ITenantDatabaseProvisioner provisioner,
        ITenantDatabaseCredentialStore credentialStore,
        ITenantPreparationOperationFence fence,
        ITenantPreparationDatabaseResetExecution execution)
    {
        this.options = options.Value;
        this.provisioner = provisioner;
        this.credentialStore = credentialStore;
        this.fence = fence;
        this.execution = execution;
    }

    public async Task<TenantPreparationDatabaseResetResult> ResetAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var operationFence = await fence.AcquireAsync(tenant, cancellationToken);
        var normalizedAdminConnectionString = PostgresConnectionString.Normalize(options.AdminConnectionString);
        await execution.ResetAsync(
            normalizedAdminConnectionString,
            tenant,
            provisioner,
            credentialStore,
            cancellationToken);

        return new TenantPreparationDatabaseResetResult(
            "reset",
            DateTimeOffset.UtcNow);
    }
}

internal interface ITenantPreparationDatabaseResetExecution
{
    Task ResetAsync(
        string adminConnectionString,
        ResolvedTenantEnvironment tenant,
        ITenantDatabaseProvisioner provisioner,
        ITenantDatabaseCredentialStore credentialStore,
        CancellationToken cancellationToken);
}

internal sealed class PostgresTenantPreparationDatabaseResetExecution : ITenantPreparationDatabaseResetExecution
{
    public async Task ResetAsync(
        string adminConnectionString,
        ResolvedTenantEnvironment tenant,
        ITenantDatabaseProvisioner provisioner,
        ITenantDatabaseCredentialStore credentialStore,
        CancellationToken cancellationToken)
    {
        await using var adminConnection = new NpgsqlConnection(adminConnectionString);
        await adminConnection.OpenAsync(cancellationToken);

        await using (var terminateCommand = adminConnection.CreateCommand())
        {
            terminateCommand.CommandText = """
                select pg_terminate_backend(pid)
                from pg_stat_activity
                where datname = @databaseName
                  and pid <> pg_backend_pid()
                """;
            terminateCommand.Parameters.AddWithValue("databaseName", tenant.DatabaseName);
            await terminateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var dropCommand = adminConnection.CreateCommand())
        {
            dropCommand.CommandText = $"drop database if exists {QuoteIdentifier(tenant.DatabaseName)} with (force)";
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var createCommand = adminConnection.CreateCommand())
        {
            createCommand.CommandText = $"create database {QuoteIdentifier(tenant.DatabaseName)}";
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // pg_terminate_backend invalidates pooled target connectors. Clear only pools for this exact
        // ERP database before any caller can reuse a killed connector.
        ClearExactPool(PostgresConnectionString.Normalize(adminConnectionString, tenant.DatabaseName));
        if (!string.IsNullOrWhiteSpace(tenant.DatabaseConnectionString))
        {
            ClearExactPool(PostgresConnectionString.Normalize(tenant.DatabaseConnectionString, tenant.DatabaseName));
        }

        // Re-establish the app role/credential mapping while the cross-instance reset lock is held.
        await provisioner.ProvisionAsync(tenant, cancellationToken);
        if (credentialStore.TryGet(tenant, out var credential))
        {
            ClearExactPool(PostgresConnectionString.Normalize(credential.ConnectionString, tenant.DatabaseName));
        }

        await using var tenantConnection = new NpgsqlConnection(
            PostgresConnectionString.Normalize(adminConnectionString, tenant.DatabaseName));
        await tenantConnection.OpenAsync(cancellationToken);
        await using var transaction = await tenantConnection.BeginTransactionAsync(cancellationToken);
        await using (var schemasCommand = new NpgsqlCommand(
            "create schema if not exists _runtime; create schema if not exists app; create schema if not exists audit;",
            tenantConnection,
            transaction))
        {
            await schemasCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await provisioner.GrantRuntimePrivilegesAsync(tenant, tenantConnection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void ClearExactPool(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        using var targetConnection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(targetConnection);
    }
}
