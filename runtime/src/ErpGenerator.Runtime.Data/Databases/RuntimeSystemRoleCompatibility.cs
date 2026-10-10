using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Data.Databases;

internal static class RuntimeSystemRoleCompatibility
{
    public static async Task EnsureAndMigrateTenantOwnerAsync(
        NpgsqlConnection connection,
        long userId,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using (var probe = new NpgsqlCommand(
            """
            select exists (
              select 1
              from _runtime.runtime_users users
              join _runtime.activation_tokens token
                on token.runtime_user_id is null
               and token.consumed_at is not null
               and token.email is not null
               and lower(token.email) = lower(users.email)
              where users.id = @userId
                and lower(users.role) = 'owner'
            )
            """,
            connection,
            transaction))
        {
            probe.Parameters.AddWithValue("userId", userId);
            if (await probe.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return;
            }
        }

        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.runtime_roles (id, display_name, updated_at)
            values (@systemRole, 'Tenant owner (system)', now())
            on conflict (id) do nothing;

            update _runtime.runtime_users u
            set role = @systemRole
            where u.id = @userId
              and lower(u.role) = 'owner'
              and exists (
                select 1
                from _runtime.activation_tokens token
                where token.runtime_user_id is null
                  and token.consumed_at is not null
                  and token.email is not null
                  and lower(token.email) = lower(u.email)
              );

            insert into _runtime.runtime_user_roles (user_id, role_id)
            select u.id, @systemRole
            from _runtime.runtime_users u
            where u.id = @userId
              and lower(u.role) = @systemRole
            on conflict do nothing;

            delete from _runtime.runtime_user_roles roles
            using _runtime.runtime_users users
            where roles.user_id = users.id
              and users.id = @userId
              and lower(users.role) = @systemRole
              and lower(roles.role_id) = 'owner';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("systemRole", RuntimeSystemRoleIds.TenantOwner);
        command.Parameters.AddWithValue("userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
