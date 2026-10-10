using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.DeploymentLocks;

public sealed class TenantDeploymentLockService(
    ITenantDatabaseConnectionFactory connectionFactory,
    IOptions<TenantDeploymentLockOptions> options,
    ILogger<TenantDeploymentLockService>? logger = null)
{
    private const string LockedMessage = "Another deployment operation is already in progress for this tenant environment.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TenantDeploymentLockOptions options = options.Value;

    public async Task<TenantDeploymentLockHandle> AcquireOrThrowAsync(
        ResolvedTenantEnvironment tenant,
        string environment,
        string operation,
        CancellationToken cancellationToken = default)
    {
        var result = await TryAcquireAsync(tenant, environment, operation, cancellationToken);
        if (result.Handle is not null)
        {
            return result.Handle;
        }

        throw new TenantDeploymentLockException(
            Normalize(environment),
            Normalize(operation),
            result.Message ?? LockedMessage,
            result.ExistingOperation,
            result.ExistingExpiresAt);
    }

    public async Task<TenantDeploymentLockResult> TryAcquireAsync(
        ResolvedTenantEnvironment tenant,
        string environment,
        string operation,
        CancellationToken cancellationToken = default)
    {
        var normalizedEnvironment = Normalize(environment);
        var normalizedOperation = Normalize(operation);
        var lockKey = BuildLockKey(tenant, normalizedEnvironment);
        var ownerId = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaOutsideTransactionAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await ReadHeldLockAsync(connection, transaction, lockKey, cancellationToken);
        if (existing is not null)
        {
            if (!existing.IsExpired)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger?.LogWarning(
                    "Deployment lock rejected for tenant {TenantId} environment {Environment}; existing operation {ExistingOperation} expires at {ExpiresAt}.",
                    tenant.TenantStableId,
                    normalizedEnvironment,
                    existing.Operation,
                    existing.ExpiresAt);
                return TenantDeploymentLockResult.Locked(existing.Operation, existing.ExpiresAt, LockedMessage);
            }

            await MarkExpiredAsync(connection, transaction, existing.LockId, cancellationToken);
            logger?.LogWarning(
                "Expired stale deployment lock {LockId} for tenant {TenantId} environment {Environment}.",
                existing.LockId,
                tenant.TenantStableId,
                normalizedEnvironment);
        }

        var lockId = Guid.NewGuid();
        DateTimeOffset acquiredAt;
        DateTimeOffset expiresAt;
        try
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into _runtime.deployment_locks
                  (id, lock_key, tenant_id, environment, operation, owner_id, status, acquired_at, expires_at, metadata)
                values
                  (@id, @lockKey, @tenantId, @environment, @operation, @ownerId, 'held', now(),
                   now() + (@ttlMinutes * interval '1 minute'), cast(@metadata as jsonb))
                returning acquired_at, expires_at
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("id", lockId);
            insert.Parameters.AddWithValue("lockKey", lockKey);
            insert.Parameters.AddWithValue("tenantId", tenant.TenantStableId);
            insert.Parameters.AddWithValue("environment", normalizedEnvironment);
            insert.Parameters.AddWithValue("operation", normalizedOperation);
            insert.Parameters.AddWithValue("ownerId", ownerId);
            insert.Parameters.AddWithValue("ttlMinutes", Math.Max(1, options.LockTtlMinutes));
            insert.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new { tenant.TenantSlug, tenant.Host }, JsonOptions));
            await using (var reader = await insert.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("The deployment lock insert did not return its authoritative lease timestamps.");
                }

                acquiredAt = ReadTimestamp(reader, 0);
                expiresAt = ReadTimestamp(reader, 1);
            }

            await transaction.CommitAsync(cancellationToken);
            logger?.LogInformation(
                "Deployment lock {LockId} acquired for tenant {TenantId} environment {Environment} operation {Operation}; expires at {ExpiresAt}.",
                lockId,
                tenant.TenantStableId,
                normalizedEnvironment,
                normalizedOperation,
                expiresAt);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger?.LogWarning(
                "Deployment lock rejected for tenant {TenantId} environment {Environment} operation {Operation} because another holder won the race.",
                tenant.TenantStableId,
                normalizedEnvironment,
                normalizedOperation);
            return TenantDeploymentLockResult.Locked(null, null, LockedMessage);
        }

        return TenantDeploymentLockResult.Success(new TenantDeploymentLockHandle(
            lockId,
            lockKey,
            tenant,
            normalizedEnvironment,
            normalizedOperation,
            ownerId,
            acquiredAt,
            expiresAt));
    }

    public async Task ReleaseAsync(
        TenantDeploymentLockHandle handle,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(handle.Tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaOutsideTransactionAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            update _runtime.deployment_locks
            set status = 'released',
                released_at = now()
            where id = @id
              and status = 'held'
              and owner_id = @ownerId
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", handle.LockId);
        command.Parameters.AddWithValue("ownerId", handle.OwnerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger?.LogInformation(
            "Deployment lock {LockId} released for tenant {TenantId} environment {Environment} operation {Operation}.",
            handle.LockId,
            handle.Tenant.TenantStableId,
            handle.Environment,
            handle.Operation);
    }

    public async Task<TenantDeploymentLockHandle> RenewOrThrowAsync(
        TenantDeploymentLockHandle handle,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(
            connectionFactory.CreateConnectionString(
                handle.Tenant,
                TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaOutsideTransactionAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update _runtime.deployment_locks
            set expires_at = now() + (@ttlMinutes * interval '1 minute')
            where id = @id
              and lock_key = @lockKey
              and status = 'held'
              and owner_id = @ownerId
              and expires_at > now()
            returning expires_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("ttlMinutes", Math.Max(1, options.LockTtlMinutes));
        command.Parameters.AddWithValue("id", handle.LockId);
        command.Parameters.AddWithValue("lockKey", handle.LockKey);
        command.Parameters.AddWithValue("ownerId", handle.OwnerId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var expiresAt = result switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(value.ToUniversalTime()),
            _ => (DateTimeOffset?)null
        };
        if (expiresAt is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw Lost(handle);
        }

        await transaction.CommitAsync(cancellationToken);
        logger?.LogInformation(
            "Deployment lock {LockId} renewed for tenant {TenantId} environment {Environment} operation {Operation}; expires at {ExpiresAt}.",
            handle.LockId,
            handle.Tenant.TenantStableId,
            handle.Environment,
            handle.Operation,
            expiresAt.Value);
        return handle with { ExpiresAt = expiresAt.Value };
    }

    internal static async Task EnsureHeldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantDeploymentLockHandle handle,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select 1
            from _runtime.deployment_locks
            where id = @id
              and lock_key = @lockKey
              and status = 'held'
              and owner_id = @ownerId
              and expires_at > now()
            for update
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", handle.LockId);
        command.Parameters.AddWithValue("lockKey", handle.LockKey);
        command.Parameters.AddWithValue("ownerId", handle.OwnerId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw Lost(handle);
        }
    }

    internal static async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in SchemaStatements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureSchemaOutsideTransactionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var statement in SchemaStatements)
        {
            try
            {
                await using var command = new NpgsqlCommand(statement, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState is
                PostgresErrorCodes.DuplicateObject or
                PostgresErrorCodes.DuplicateTable or
                PostgresErrorCodes.UniqueViolation)
            {
                // Concurrent first-use acquisition may race on schema/table/index creation.
                // The statements are idempotent, so the winner's object is the desired state.
            }
        }
    }

    public static IReadOnlyList<string> SchemaStatements { get; } =
    [
        "create schema if not exists _runtime",
        """
        create table if not exists _runtime.deployment_locks (
          id uuid primary key,
          lock_key varchar(220) not null,
          tenant_id text,
          environment varchar(40) not null,
          operation varchar(40) not null,
          owner_id varchar(120) not null,
          status varchar(40) not null,
          acquired_at timestamptz not null default now(),
          expires_at timestamptz not null,
          released_at timestamptz,
          metadata jsonb not null default '{}'::jsonb
        )
        """,
        """
        create unique index if not exists ux_deployment_locks_held
        on _runtime.deployment_locks(lock_key)
        where status = 'held'
        """
    ];

    private static async Task<ExistingLock?> ReadHeldLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lockKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, operation, expires_at, expires_at <= now()
            from _runtime.deployment_locks
            where lock_key = @lockKey and status = 'held'
            order by acquired_at desc
            limit 1
            for update
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("lockKey", lockKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingLock(
            reader.GetGuid(0),
            reader.GetString(1),
            ReadTimestamp(reader, 2),
            reader.GetBoolean(3));
    }

    private static async Task MarkExpiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid lockId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update _runtime.deployment_locks
            set status = 'expired',
                released_at = now()
            where id = @id and status = 'held'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", lockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildLockKey(ResolvedTenantEnvironment tenant, string environment) =>
        $"{tenant.TenantStableId}:{environment}";

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(timestamp.ToUniversalTime()),
            _ => throw new InvalidOperationException("The deployment lock timestamp has an unexpected database type.")
        };
    }

    private static TenantDeploymentLockException Lost(TenantDeploymentLockHandle handle) =>
        new(
            handle.Environment,
            handle.Operation,
            "The deployment lock lease was lost before the operation reached its authoritative mutation boundary.");

    private sealed record ExistingLock(
        Guid LockId,
        string Operation,
        DateTimeOffset ExpiresAt,
        bool IsExpired);
}
