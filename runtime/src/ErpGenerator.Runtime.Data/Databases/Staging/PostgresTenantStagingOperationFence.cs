using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Staging;

public sealed class PostgresTenantStagingOperationFence : ITenantStagingOperationFence
{
    private static readonly Regex DatabaseNamePattern = new("^[a-z][a-z0-9_]{2,62}$", RegexOptions.Compiled);
    private readonly RuntimeDatabaseOptions options;
    private readonly ITenantStagingAdvisoryLockExecution execution;

    public PostgresTenantStagingOperationFence(IOptions<RuntimeDatabaseOptions> options)
        : this(options, new PostgresTenantStagingAdvisoryLockExecution())
    {
    }

    internal PostgresTenantStagingOperationFence(
        IOptions<RuntimeDatabaseOptions> options,
        ITenantStagingAdvisoryLockExecution execution)
    {
        this.options = options.Value;
        this.execution = execution;
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.Environment != TenantEnvironmentKind.Staging)
        {
            throw new InvalidOperationException("Only a tenant staging database can use the staging operation fence.");
        }

        var expectedDatabaseName = TenantDatabaseNamePolicy.BuildExpected(
            tenant.TenantStableId,
            TenantEnvironmentKind.Staging);
        if (!DatabaseNamePattern.IsMatch(tenant.DatabaseName) ||
            !string.Equals(tenant.DatabaseName, expectedDatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Tenant staging database identity does not match its immutable stable id.");
        }

        if (!options.AutoProvisionDatabases)
        {
            throw new InvalidOperationException("Automatic tenant database provisioning is required for staging lifecycle operations.");
        }

        if (string.IsNullOrWhiteSpace(options.AdminConnectionString))
        {
            throw new InvalidOperationException("An administrative database connection is required for staging lifecycle operations.");
        }

        var normalizedAdminConnectionString = PostgresConnectionString.Normalize(options.AdminConnectionString);
        var adminBuilder = new NpgsqlConnectionStringBuilder(normalizedAdminConnectionString);
        if (string.Equals(adminBuilder.Database, expectedDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The administrative database cannot be the staging lifecycle target.");
        }

        return await execution.AcquireAsync(
            normalizedAdminConnectionString,
            AdvisoryLockKey(expectedDatabaseName),
            cancellationToken);
    }

    internal static long AdvisoryLockKey(string databaseName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"erpgenerator:staging-operation:{databaseName}"));
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }
}

internal interface ITenantStagingAdvisoryLockExecution
{
    Task<IAsyncDisposable> AcquireAsync(
        string adminConnectionString,
        long advisoryLockKey,
        CancellationToken cancellationToken);
}

internal sealed class PostgresTenantStagingAdvisoryLockExecution : ITenantStagingAdvisoryLockExecution
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
                throw new TenantStagingOperationConflictException();
            }

            return new PostgresTenantStagingOperationFenceHandle(connection, advisoryLockKey);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class PostgresTenantStagingOperationFenceHandle(
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

