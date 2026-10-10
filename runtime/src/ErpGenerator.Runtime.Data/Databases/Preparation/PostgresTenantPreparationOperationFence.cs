using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Preparation;

public sealed class PostgresTenantPreparationOperationFence : ITenantPreparationOperationFence
{
    private static readonly Regex DatabaseNamePattern = new("^[a-z][a-z0-9_]{2,62}$", RegexOptions.Compiled);
    private readonly RuntimeDatabaseOptions options;
    private readonly ITenantPreparationAdvisoryLockExecution execution;

    public PostgresTenantPreparationOperationFence(IOptions<RuntimeDatabaseOptions> options)
        : this(options, new PostgresTenantPreparationAdvisoryLockExecution())
    {
    }

    internal PostgresTenantPreparationOperationFence(
        IOptions<RuntimeDatabaseOptions> options,
        ITenantPreparationAdvisoryLockExecution execution)
    {
        this.options = options.Value;
        this.execution = execution;
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.Environment != TenantEnvironmentKind.Erp)
        {
            throw new InvalidOperationException("Only a tenant ERP database can use the preparation operation fence.");
        }

        if (!DatabaseNamePattern.IsMatch(tenant.DatabaseName) ||
            !TenantDatabaseNamePolicy.IsExpected(tenant.TenantStableId, tenant.DatabaseName))
        {
            throw new InvalidOperationException("Tenant ERP database identity does not match its immutable stable id.");
        }

        if (!options.AutoProvisionDatabases)
        {
            throw new InvalidOperationException("Automatic tenant database provisioning is required for ERP preparation operations.");
        }

        if (string.IsNullOrWhiteSpace(options.AdminConnectionString))
        {
            throw new InvalidOperationException("An administrative database connection is required for ERP preparation operations.");
        }

        var normalizedAdminConnectionString = PostgresConnectionString.Normalize(options.AdminConnectionString);
        var adminBuilder = new NpgsqlConnectionStringBuilder(normalizedAdminConnectionString);
        if (string.Equals(adminBuilder.Database, tenant.DatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The administrative database cannot be the ERP preparation target.");
        }

        return await execution.AcquireAsync(
            normalizedAdminConnectionString,
            AdvisoryLockKey(tenant.DatabaseName),
            cancellationToken);
    }

    internal static long AdvisoryLockKey(string databaseName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"erpgenerator:staging-operation:{databaseName}"));
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }
}

internal interface ITenantPreparationAdvisoryLockExecution
{
    Task<IAsyncDisposable> AcquireAsync(
        string adminConnectionString,
        long advisoryLockKey,
        CancellationToken cancellationToken);
}

internal sealed class PostgresTenantPreparationAdvisoryLockExecution : ITenantPreparationAdvisoryLockExecution
{
    public async Task<IAsyncDisposable> AcquireAsync(
        string adminConnectionString,
        long advisoryLockKey,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(adminConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "select pg_try_advisory_lock(@lockKey)";
            command.Parameters.AddWithValue("lockKey", advisoryLockKey);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new TenantPreparationOperationConflictException();
            }

            return new PostgresTenantPreparationOperationFenceHandle(connection, advisoryLockKey);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class PostgresTenantPreparationOperationFenceHandle(
    NpgsqlConnection connection,
    long advisoryLockKey) : IAsyncDisposable
{
    private int disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (connection.State == System.Data.ConnectionState.Open)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "select pg_advisory_unlock(@lockKey)";
                command.Parameters.AddWithValue("lockKey", advisoryLockKey);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                // Closing the owning session below is the authoritative lock release.
            }
        }

        await connection.DisposeAsync();
    }
}
