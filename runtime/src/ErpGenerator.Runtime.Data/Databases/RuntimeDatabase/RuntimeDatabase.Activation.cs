using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    public async Task<int> ExpirePendingOwnerActivationTokensAsync(
        ResolvedTenantEnvironment tenant,
        string? email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return 0;
        }

        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.activation_tokens
            set expires_at = now()
            where runtime_user_id is null
              and consumed_at is null
              and expires_at > now()
              and email is not null
              and lower(email) = @email
            """;
        command.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> CreateActivationTokenAsync(
        ResolvedTenantEnvironment tenant,
        string? email,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
        var hash = Sha256(token);

        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into _runtime.activation_tokens (token_hash, email, expires_at)
            values (@hash, @email, @expiresAt)
            """;
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);
        command.Parameters.AddWithValue("expiresAt", DateTimeOffset.UtcNow.Add(lifetime));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return token;
    }

    public async Task<bool> ActivateOwnerAsync(
        ResolvedTenantEnvironment tenant,
        string token,
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = Sha256(token);

        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long? activationId = null;
        string? activationEmail = null;
        await using (var command = new NpgsqlCommand(
            """
            select id, email
            from _runtime.activation_tokens
            where token_hash = @hash
              and runtime_user_id is null
              and consumed_at is null
              and expires_at > now()
            for update
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("hash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                activationId = reader.GetInt64(0);
                activationEmail = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (activationId is null ||
            string.IsNullOrWhiteSpace(activationEmail) ||
            !string.Equals(activationEmail.Trim(), normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        long userId;
        await using (var command = new NpgsqlCommand(
            """
            insert into _runtime.runtime_users (email, display_name, password_hash, role, status, activated_at)
            values (@email, @displayName, @passwordHash, @systemRole, 'active', now())
            on conflict (email) do update set
              display_name = excluded.display_name,
              password_hash = excluded.password_hash,
              role = @systemRole,
              status = 'active',
              activated_at = coalesce(_runtime.runtime_users.activated_at, now())
            returning id
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("email", normalizedEmail);
            command.Parameters.AddWithValue("displayName", displayName.Trim());
            command.Parameters.AddWithValue("passwordHash", PasswordHasher.Hash(password));
            command.Parameters.AddWithValue("systemRole", RuntimeSystemRoleIds.TenantOwner);
            userId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        await EnsureTenantOwnerRuntimeRoleAsync(connection, transaction, cancellationToken);

        await using (var command = new NpgsqlCommand(
            """
            insert into _runtime.runtime_user_roles (user_id, role_id)
            values (@userId, @systemRole)
            on conflict do nothing
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("userId", userId);
            command.Parameters.AddWithValue("systemRole", RuntimeSystemRoleIds.TenantOwner);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand(
            "delete from _runtime.runtime_user_roles where user_id = @userId and lower(role_id) = 'owner'",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("userId", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand(
            "update _runtime.activation_tokens set consumed_at = now() where id = @id",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", activationId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task EnsureTenantOwnerRuntimeRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.runtime_roles (id, display_name, updated_at)
            values (@systemRole, 'Tenant owner (system)', now())
            on conflict (id) do update set
              display_name = excluded.display_name,
              updated_at = now()
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("systemRole", RuntimeSystemRoleIds.TenantOwner);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
