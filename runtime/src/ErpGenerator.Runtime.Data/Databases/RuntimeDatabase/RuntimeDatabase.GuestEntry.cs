using System.Security.Cryptography;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Shared.Runtime;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    public async Task<string> CreateGuestEntryAsync(ResolvedTenantEnvironment tenant, string customerId, string displayName, TimeSpan lifetime, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureTenantOwnerRuntimeRoleAsync(connection, transaction, ct);
        var compiled = await ReadActiveCompiledBundleAsync(tenant, ct);
        var role = compiled?.Roles.Any(v => v.Id == "demo_operator") == true ? "demo_operator" : RuntimeSystemRoleIds.TenantOwner;
        var email = $"guest-{customerId}@demo.invalid";
        long userId;
        await using (var command = new NpgsqlCommand("""
            insert into _runtime.runtime_users (email, display_name, role, status, activated_at)
            values (@email, @name, @role, 'active', now())
            on conflict (email) do update set display_name = excluded.display_name
            returning id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("email", email);
            command.Parameters.AddWithValue("name", displayName);
            command.Parameters.AddWithValue("role", role);
            userId = (long)(await command.ExecuteScalarAsync(ct))!;
        }
        await using (var command = new NpgsqlCommand("insert into _runtime.runtime_user_roles (user_id, role_id) values (@id, @role) on conflict do nothing", connection, transaction))
        {
            command.Parameters.AddWithValue("id", userId);
            command.Parameters.AddWithValue("role", role);
            await command.ExecuteNonQueryAsync(ct);
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using (var command = new NpgsqlCommand("""
            insert into _runtime.guest_entry_tokens (token_hash, tenant_id, tenant_stable_id, environment, host, runtime_user_id, expires_at)
            values (@hash, @tenant, @stable, @env, @host, @user, @expires)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("hash", Sha256(token));
            command.Parameters.AddWithValue("tenant", tenant.TenantId);
            command.Parameters.AddWithValue("stable", tenant.TenantStableId);
            command.Parameters.AddWithValue("env", tenant.Environment.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("host", tenant.Host);
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("expires", DateTimeOffset.UtcNow.Add(lifetime));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return token;
    }

    public async Task<RuntimeUser?> ConsumeGuestEntryAsync(ResolvedTenantEnvironment tenant, string token, CancellationToken ct = default)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(ct);
        long? userId;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                update _runtime.guest_entry_tokens set consumed_at = now()
                where token_hash = @hash and consumed_at is null and expires_at > now()
                  and tenant_id = @tenant and tenant_stable_id = @stable and environment = @env and host = @host
                  and exists (select 1 from _runtime.runtime_users u where u.id = runtime_user_id and u.status = 'active')
                returning runtime_user_id
                """;
            command.Parameters.AddWithValue("hash", Sha256(token));
            command.Parameters.AddWithValue("tenant", tenant.TenantId);
            command.Parameters.AddWithValue("stable", tenant.TenantStableId);
            command.Parameters.AddWithValue("env", tenant.Environment.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("host", tenant.Host);
            userId = await command.ExecuteScalarAsync(ct) as long?;
        }
        if (userId is null) return null;
        RuntimeUser user;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "select id, email, display_name, role, status from _runtime.runtime_users where id = @id and status = 'active'";
            command.Parameters.AddWithValue("id", userId.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            user = new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), "", reader.GetString(3), reader.GetString(4), []);
        }
        return user with { Roles = await LoadUserRolesAsync(connection, user.Id, user.Role, ct) };
    }
}
