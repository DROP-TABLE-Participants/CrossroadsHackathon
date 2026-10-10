using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    public async Task<RuntimeUser?> ValidateLoginAsync(
        ResolvedTenantEnvironment tenant,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, email, display_name, password_hash, role, status
            from _runtime.runtime_users
            where email = @email
              and status = 'active'
              and password_hash is not null
            """;
        command.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var user = new RuntimeUser(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            []);

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            return null;
        }

        await reader.DisposeAsync();
        if (user.Role.Equals("owner", StringComparison.OrdinalIgnoreCase))
        {
            await RuntimeSystemRoleCompatibility.EnsureAndMigrateTenantOwnerAsync(connection, user.Id, cancellationToken: cancellationToken);
            await using var roleCommand = connection.CreateCommand();
            roleCommand.CommandText = "select role from _runtime.runtime_users where id = @id";
            roleCommand.Parameters.AddWithValue("id", user.Id);
            user = user with
            {
                Role = Convert.ToString(await roleCommand.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
                       ?? user.Role
            };
        }

        var roles = await LoadUserRolesAsync(connection, user.Id, user.Role, cancellationToken);
        await using var updateLogin = connection.CreateCommand();
        updateLogin.CommandText = "update _runtime.runtime_users set last_login_at = now() where id = @id";
        updateLogin.Parameters.AddWithValue("id", user.Id);
        await updateLogin.ExecuteNonQueryAsync(cancellationToken);
        return user with
        {
            Roles = roles
        };
    }

    private static async Task<IReadOnlyList<string>> LoadUserRolesAsync(
        NpgsqlConnection connection,
        long userId,
        string fallbackRole,
        CancellationToken cancellationToken)
    {
        var roles = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select role_id
            from _runtime.runtime_user_roles
            where user_id = @userId
            order by case lower(role_id)
                when '_runtime_owner' then 0
                when 'owner' then 0
                when 'admin' then 2
                when 'member' then 3
                when 'readonly' then 4
                else 10
            end, role_id
            """;
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(reader.GetString(0).ToLowerInvariant());
        }

        if (roles.Count == 0)
        {
            roles.Add(fallbackRole.ToLowerInvariant());
        }

        return roles;
    }
}
