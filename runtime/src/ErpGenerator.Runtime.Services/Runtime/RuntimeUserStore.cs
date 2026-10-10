using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed class RuntimeUserStore : IRuntimeUserService
{
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
    private readonly ITenantDatabaseConnectionFactory connectionFactory;
    private readonly RuntimePermissionService permissions;

    public RuntimeUserStore(ITenantDatabaseConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
        permissions = new RuntimePermissionService(connectionFactory);
    }

    public async Task<RuntimeUserContext?> GetActiveUserContextAsync(
        ResolvedTenantEnvironment tenant,
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var user = await LoadUserAsync(connection, userId, cancellationToken);
        if (user is not null && user.Role.Equals("owner", StringComparison.OrdinalIgnoreCase))
        {
            await RuntimeSystemRoleCompatibility.EnsureAndMigrateTenantOwnerAsync(connection, user.Id, cancellationToken: cancellationToken);
            user = await LoadUserAsync(connection, userId, cancellationToken);
        }

        if (user is null || !user.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var roles = await LoadUserRolesAsync(connection, user.Id, user.Role, cancellationToken);
        var primaryRole = SelectPrimaryRole(roles, user.Role);
        return new RuntimeUserContext(user.Id, user.Email, primaryRole)
        {
            Roles = roles
        };
    }

    public async Task<IReadOnlyList<RuntimeManagedUser>> ListUsersAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var rows = new List<RuntimeUserRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, email, display_name, role, status, activated_at, last_login_at, created_at
            from _runtime.runtime_users
            order by id
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RuntimeUserRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        await reader.DisposeAsync();

        var users = new List<RuntimeManagedUser>();
        foreach (var user in rows)
        {
            users.Add(new RuntimeManagedUser(
                user.Id,
                user.Email,
                user.DisplayName,
                user.Status,
                await LoadUserRolesAsync(connection, user.Id, user.Role, cancellationToken),
                await LoadUserLinkAsync(connection, user.Id, cancellationToken),
                user.ActivatedAt,
                user.LastLoginAt,
                user.CreatedAt));
        }

        return users;
    }

    public async Task<IReadOnlyList<RuntimeRoleOption>> ListRolesAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var roles = new List<RuntimeRoleOption>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, display_name
            from _runtime.runtime_roles
            where lower(id) <> @systemRole
            order by case lower(id)
                when 'owner' then 0
                when 'admin' then 1
                when 'member' then 2
                when 'readonly' then 3
                else 10
            end, id
            """;
        command.Parameters.AddWithValue("systemRole", RuntimeSystemRoleIds.TenantOwner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new RuntimeRoleOption(reader.GetString(0), reader.GetString(1)));
        }

        return roles;
    }

    public async Task<RuntimeUserInviteResult> InviteUserAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        string email,
        string displayName,
        IReadOnlyList<string>? roles,
        long? employeeRecordId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedName = string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName.Trim();
        var roleIds = NormalizeRoles(roles);

        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureRolesExistAsync(connection, transaction, roleIds, cancellationToken);
        var existing = await LoadUserByEmailAsync(connection, transaction, normalizedEmail, cancellationToken);
        long userId;
        string status;
        if (existing is null)
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into _runtime.runtime_users (email, display_name, password_hash, role, status)
                values (@email, @displayName, null, @role, 'pending')
                returning id
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("email", normalizedEmail);
            insert.Parameters.AddWithValue("displayName", normalizedName);
            insert.Parameters.AddWithValue("role", roleIds[0]);
            userId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            status = "pending";
        }
        else
        {
            userId = existing.Id;
            status = existing.Status;
            await using var update = new NpgsqlCommand(
                """
                update _runtime.runtime_users
                set display_name = @displayName,
                    role = @role,
                    status = case when status = 'pending' then 'pending' else status end
                where id = @id
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("id", userId);
            update.Parameters.AddWithValue("displayName", normalizedName);
            update.Parameters.AddWithValue("role", roleIds[0]);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceRolesAsync(connection, transaction, userId, roleIds, cancellationToken);
        if (employeeRecordId is not null)
        {
            await LinkEmployeeAsync(connection, transaction, tenant, actor, userId, employeeRecordId.Value, cancellationToken);
        }

        string? token = null;
        if (!status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            token = await InsertActivationTokenAsync(connection, transaction, userId, normalizedEmail, actor.UserId, lifetime, cancellationToken);
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            "runtime_user.invited",
            "Runtime user invited.",
            new Dictionary<string, object?>
            {
                ["runtimeUserId"] = userId,
                ["email"] = normalizedEmail,
                ["roles"] = roleIds
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new RuntimeUserInviteResult(userId, token);
    }

    public async Task<bool> ActivateInvitedUserAsync(
        ResolvedTenantEnvironment tenant,
        string token,
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = Sha256(token);
        var normalizedEmail = NormalizeEmail(email);

        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long? activationId = null;
        long? userId = null;
        string? tokenEmail = null;
        await using (var command = new NpgsqlCommand(
            """
            select id, runtime_user_id, email
            from _runtime.activation_tokens
            where token_hash = @hash
              and runtime_user_id is not null
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
                userId = reader.GetInt64(1);
                tokenEmail = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        if (activationId is null || userId is null ||
            (!string.IsNullOrWhiteSpace(tokenEmail) && !normalizedEmail.Equals(tokenEmail, StringComparison.OrdinalIgnoreCase)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var update = new NpgsqlCommand(
            """
            update _runtime.runtime_users
            set email = @email,
                display_name = @displayName,
                password_hash = @passwordHash,
                status = 'active',
                activated_at = coalesce(activated_at, now())
            where id = @id
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("id", userId.Value);
            update.Parameters.AddWithValue("email", normalizedEmail);
            update.Parameters.AddWithValue("displayName", string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName.Trim());
            update.Parameters.AddWithValue("passwordHash", PasswordHasher.Hash(password));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var consume = new NpgsqlCommand(
            "update _runtime.activation_tokens set consumed_at = now() where id = @id",
            connection,
            transaction))
        {
            consume.Parameters.AddWithValue("id", activationId.Value);
            await consume.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            null,
            "runtime_user.activated",
            "Runtime user activated.",
            new Dictionary<string, object?>
            {
                ["runtimeUserId"] = userId.Value,
                ["email"] = normalizedEmail
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateUserAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        string? displayName,
        IReadOnlyList<string>? roles,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var user = await LoadUserAsync(connection, transaction, userId, cancellationToken);
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            await using var update = new NpgsqlCommand(
                "update _runtime.runtime_users set display_name = @displayName where id = @id",
                connection,
                transaction);
            update.Parameters.AddWithValue("id", userId);
            update.Parameters.AddWithValue("displayName", displayName.Trim());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (roles is not null)
        {
            var roleIds = NormalizeRoles(roles);
            await EnsureRolesExistAsync(connection, transaction, roleIds, cancellationToken);
            var existingRoles = await LoadUserRolesAsync(connection, userId, user.Role, cancellationToken, transaction);
            var preservedSystemRoles = existingRoles.Where(RuntimeSystemRoleIds.IsReserved);
            var effectiveRoles = preservedSystemRoles
                .Concat(roleIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await ReplaceRolesAsync(connection, transaction, userId, effectiveRoles, cancellationToken);
            await using var updatePrimary = new NpgsqlCommand(
                "update _runtime.runtime_users set role = @role where id = @id",
                connection,
                transaction);
            updatePrimary.Parameters.AddWithValue("id", userId);
            updatePrimary.Parameters.AddWithValue("role", SelectPrimaryRole(effectiveRoles, user.Role));
            await updatePrimary.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            "runtime_user.updated",
            "Runtime user updated.",
            new Dictionary<string, object?> { ["runtimeUserId"] = userId },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AddRoleAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        var roleId = NormalizeAssignableRole(role);
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await LoadUserAsync(connection, transaction, userId, cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await EnsureRolesExistAsync(connection, transaction, [roleId], cancellationToken);
        await using (var command = new NpgsqlCommand(
            "insert into _runtime.runtime_user_roles (user_id, role_id) values (@userId, @role) on conflict do nothing",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("userId", userId);
            command.Parameters.AddWithValue("role", roleId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            "runtime_user.role_added",
            "Runtime user role added.",
            new Dictionary<string, object?> { ["runtimeUserId"] = userId, ["role"] = roleId },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveRoleAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        var roleId = NormalizeAssignableRole(role);
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await LoadUserAsync(connection, transaction, userId, cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var command = new NpgsqlCommand(
            "delete from _runtime.runtime_user_roles where user_id = @userId and role_id = @role",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("userId", userId);
            command.Parameters.AddWithValue("role", roleId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var remaining = await LoadUserRolesAsync(connection, userId, roleId, cancellationToken, transaction);
        await using (var updatePrimary = new NpgsqlCommand(
            "update _runtime.runtime_users set role = @role where id = @id",
            connection,
            transaction))
        {
            updatePrimary.Parameters.AddWithValue("id", userId);
            updatePrimary.Parameters.AddWithValue("role", SelectPrimaryRole(remaining, "readonly"));
            await updatePrimary.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            "runtime_user.role_removed",
            "Runtime user role removed.",
            new Dictionary<string, object?> { ["runtimeUserId"] = userId, ["role"] = roleId },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetUserEnabledAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update _runtime.runtime_users set status = @status where id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("status", enabled ? "active" : "disabled");
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            enabled ? "runtime_user.enabled" : "runtime_user.disabled",
            enabled ? "Runtime user enabled." : "Runtime user disabled.",
            new Dictionary<string, object?> { ["runtimeUserId"] = userId },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> LinkEmployeeAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        long employeeRecordId,
        CancellationToken cancellationToken = default)
    {
        await BindUserToRecordAsync(
            tenant,
            actor,
            userId,
            "employee",
            employeeRecordId,
            "runtime_user.linked_employee",
            "Runtime user linked to employee.",
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Binds one active runtime user to one active generated record. The stored
    /// entity key is always the canonical entity id, even when the caller uses
    /// an unambiguous entity name or table-name alias.
    /// </summary>
    public Task<RuntimeUserRecordLink> BindUserToRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        string entityKey,
        long recordId,
        CancellationToken cancellationToken = default) =>
        BindUserToRecordAsync(
            tenant,
            actor,
            userId,
            entityKey,
            recordId,
            "runtime_user.linked_record",
            "Runtime user linked to generated record.",
            cancellationToken);

    internal async Task<RuntimeUserRecordLink> BindOwnedAcceptanceActorToRecordAsync(
        ResolvedTenantEnvironment tenant,
        Guid runId,
        long userId,
        string entityId,
        long recordId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            throw new RuntimeUserValidationException("A non-empty acceptance run id is required.");
        }

        if (userId >= 0)
        {
            throw new RuntimeUserValidationException("An owned negative acceptance actor principal is required.");
        }

        EnsurePositiveId(recordId, "Record id");
        try
        {
            await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ownedActor = await LoadOwnedAcceptanceActorAsync(
                connection,
                transaction,
                runId,
                userId,
                cancellationToken);
            var entity = await ResolveLinkEntityAsync(connection, transaction, entityId, cancellationToken);
            var normalizedEntityId = entityId?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!entity.Id.Equals(normalizedEntityId, StringComparison.Ordinal))
            {
                throw new RuntimeUserValidationException(
                    $"Acceptance actor links require canonical entity id '{entity.Id}', not alias '{normalizedEntityId}'.");
            }

            var actor = new RuntimeUserContext(userId, ownedActor.Email, ownedActor.Role)
            {
                Roles = [ownedActor.Role]
            };
            var link = await BindUserToRecordAsync(
                connection,
                transaction,
                tenant,
                actor,
                userId,
                entity.Id,
                recordId,
                requireActiveUser: true,
                auditEventType: null,
                auditMessage: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return link;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RuntimeUserValidationException(
                "The requested acceptance actor-to-record link conflicts with an existing link.");
        }
    }

    public async Task<bool> UnlinkUserFromRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        string entityKey,
        long recordId,
        CancellationToken cancellationToken = default)
    {
        EnsurePositiveId(userId, "Runtime user id");
        EnsurePositiveId(recordId, "Record id");

        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureCanManageUserLinksAsync(connection, transaction, actor, cancellationToken);
        await EnsureRuntimeUserExistsAsync(connection, transaction, userId, requireActive: false, cancellationToken);
        var entity = await ResolveLinkEntityAsync(connection, transaction, entityKey, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            delete from _runtime.runtime_user_links
            where runtime_user_id = @userId
              and entity_key = @entityId
              and record_id = @recordId
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("entityId", entity.Id);
        command.Parameters.AddWithValue("recordId", recordId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (deleted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            "runtime_user.unlinked_record",
            "Runtime user unlinked from generated record.",
            new Dictionary<string, object?>
            {
                ["targetRuntimeUserId"] = userId,
                ["entityId"] = entity.Id,
                ["recordId"] = recordId
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<RuntimeUserRecordLink>> ListUserRecordLinksAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        CancellationToken cancellationToken = default)
    {
        EnsurePositiveId(userId, "Runtime user id");

        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureCanManageUserLinksAsync(connection, transaction, actor, cancellationToken);
        await EnsureRuntimeUserExistsAsync(connection, transaction, userId, requireActive: false, cancellationToken);

        var links = new List<RuntimeUserRecordLink>();
        await using var command = new NpgsqlCommand(
            """
            select entity_key, record_id, created_at, created_by
            from _runtime.runtime_user_links
            where runtime_user_id = @userId
            order by entity_key, record_id
            for share
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(ReadRecordLink(userId, reader));
        }

        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return links;
    }

    public async Task<bool> UnlinkEmployeeAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        CancellationToken cancellationToken = default)
    {
        EnsurePositiveId(userId, "Runtime user id");
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureCanManageUserLinksAsync(connection, transaction, actor, cancellationToken);
        await using var command = new NpgsqlCommand(
            "delete from _runtime.runtime_user_links where runtime_user_id = @userId and entity_key in ('employee', 'employees')",
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", userId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (deleted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            "runtime_user.unlinked_employee",
            "Runtime user unlinked from employee.",
            new Dictionary<string, object?> { ["runtimeUserId"] = userId },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<RuntimeUserRecordLink> BindUserToRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        string entityKey,
        long recordId,
        string auditEventType,
        string auditMessage,
        CancellationToken cancellationToken)
    {
        EnsurePositiveId(userId, "Runtime user id");
        EnsurePositiveId(recordId, "Record id");

        try
        {
            await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await EnsureCanManageUserLinksAsync(connection, transaction, actor, cancellationToken);
            var link = await BindUserToRecordAsync(
                connection,
                transaction,
                tenant,
                actor,
                userId,
                entityKey,
                recordId,
                requireActiveUser: true,
                auditEventType,
                auditMessage,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return link;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RuntimeUserValidationException(
                "The requested runtime user-to-record link conflicts with an existing link.");
        }
    }

    private async Task LinkEmployeeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        long employeeRecordId,
        CancellationToken cancellationToken)
    {
        await BindUserToRecordAsync(
            connection,
            transaction,
            tenant,
            actor,
            userId,
            "employee",
            employeeRecordId,
            requireActiveUser: false,
            "runtime_user.linked_employee",
            "Runtime user linked to employee.",
            cancellationToken);
    }

    private static async Task<RuntimeUserRecordLink> BindUserToRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext actor,
        long userId,
        string entityKey,
        long recordId,
        bool requireActiveUser,
        string? auditEventType,
        string? auditMessage,
        CancellationToken cancellationToken)
    {
        await EnsureRuntimeUserExistsAsync(connection, transaction, userId, requireActiveUser, cancellationToken);
        var entity = await ResolveLinkEntityAsync(connection, transaction, entityKey, cancellationToken);
        await EnsureActiveRecordExistsAsync(connection, transaction, entity, recordId, cancellationToken);

        if (IsEmployeeEntity(entity))
        {
            await AcquireEmployeeLinkLockAsync(connection, transaction, entity.Id, recordId, cancellationToken);
            await EnsureEmployeeRecordIsAvailableAsync(connection, transaction, entity.Id, userId, recordId, cancellationToken);
        }

        var existing = await LoadUserRecordLinkAsync(
            connection,
            transaction,
            userId,
            entity.Id,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.RecordId != recordId)
            {
                throw new RuntimeUserValidationException(
                    $"Runtime user '{userId}' is already linked to a different '{entity.Id}' record.");
            }

            return existing;
        }

        RuntimeUserRecordLink link;
        await using (var command = new NpgsqlCommand(
                         """
                         insert into _runtime.runtime_user_links (runtime_user_id, entity_key, record_id, created_by)
                         values (@userId, @entityId, @recordId, @createdBy)
                         returning entity_key, record_id, created_at, created_by
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("userId", userId);
            command.Parameters.AddWithValue("entityId", entity.Id);
            command.Parameters.AddWithValue("recordId", recordId);
            command.Parameters.AddWithValue("createdBy", actor.UserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Runtime user-to-record link insert returned no row.");
            }

            link = ReadRecordLink(userId, reader);
        }

        if (auditEventType is null)
        {
            return link;
        }

        var metadata = new Dictionary<string, object?>
        {
            ["targetRuntimeUserId"] = userId,
            ["entityId"] = entity.Id,
            ["recordId"] = recordId
        };
        if (IsEmployeeEntity(entity))
        {
            metadata["employeeRecordId"] = recordId;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            actor,
            auditEventType,
            auditMessage ?? "Runtime user linked to generated record.",
            metadata,
            cancellationToken);
        return link;
    }

    private static async Task<RuntimeOwnedAcceptanceActor> LoadOwnedAcceptanceActorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        long userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var ownershipCommand = new NpgsqlCommand(
                             """
                             select 1
                             from _runtime.acceptance_test_ownership
                             where id = true
                               and run_id = @runId
                               and database_name = current_database()
                             for share
                             """,
                             connection,
                             transaction))
            {
                ownershipCommand.Parameters.AddWithValue("runId", runId);
                if (await ownershipCommand.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new RuntimeUserValidationException(
                        "Acceptance database ownership is missing or belongs to another run.");
                }
            }

            await using var actorCommand = new NpgsqlCommand(
                """
                select users.email, owned.role_id
                from _runtime.acceptance_test_actor_ownership owned
                join _runtime.runtime_users users
                  on users.id = owned.runtime_user_id
                 and users.email = owned.email
                 and users.status = 'active'
                join _runtime.runtime_user_roles roles
                  on roles.user_id = users.id
                 and roles.role_id = owned.role_id
                where owned.run_id = @runId
                  and owned.runtime_user_id = @userId
                  and (
                    select count(*)
                    from _runtime.runtime_user_roles exact_roles
                    where exact_roles.user_id = users.id
                  ) = 1
                limit 1
                for share of owned, users, roles
                """,
                connection,
                transaction);
            actorCommand.Parameters.AddWithValue("runId", runId);
            actorCommand.Parameters.AddWithValue("userId", userId);
            await using var reader = await actorCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new RuntimeUserValidationException(
                    "Acceptance actor identity is not active or does not match this run's ownership marker.");
            }

            return new RuntimeOwnedAcceptanceActor(reader.GetString(0), reader.GetString(1));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new RuntimeUserValidationException(
                "Acceptance ownership metadata is not available in this database.");
        }
    }

    private async Task EnsureCanManageUserLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeUserContext actor,
        CancellationToken cancellationToken)
    {
        if (actor.UserId <= 0)
        {
            throw new RuntimeUserPermissionException("An active persisted runtime administrator is required.");
        }

        string? status;
        await using (var command = new NpgsqlCommand(
                         "select status from _runtime.runtime_users where id = @userId for share",
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("userId", actor.UserId);
            status = await command.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeUserPermissionException("An active persisted runtime administrator is required.");
        }

        var snapshot = await permissions.CreateSnapshotAsync(connection, transaction, actor, cancellationToken);
        if (!permissions.IsAllowed(snapshot, "admin", entity: null))
        {
            throw new RuntimeUserPermissionException("Runtime administrator permission is required to manage user record links.");
        }
    }

    private static async Task EnsureRuntimeUserExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        bool requireActive,
        CancellationToken cancellationToken)
    {
        string? status;
        await using (var command = new NpgsqlCommand(
                         "select status from _runtime.runtime_users where id = @userId for update",
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("userId", userId);
            status = await command.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (status is null ||
            (requireActive && !status.Equals("active", StringComparison.OrdinalIgnoreCase)))
        {
            throw new RuntimeUserValidationException(
                requireActive
                    ? $"Active runtime user '{userId}' was not found."
                    : $"Runtime user '{userId}' was not found.");
        }
    }

    private static async Task<RuntimeUserLinkEntity> ResolveLinkEntityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string entityKey,
        CancellationToken cancellationToken)
    {
        var normalizedKey = entityKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IdentifierPattern.IsMatch(normalizedKey))
        {
            throw new RuntimeUserValidationException("A valid generated entity key is required.");
        }

        var matches = new List<RuntimeUserLinkEntity>();
        await using var command = new NpgsqlCommand(
            """
            select id, name, table_name
            from _runtime.entity_definitions
            where id = @key or name = @key or table_name = @key
            order by case when id = @key then 0 when name = @key then 1 else 2 end, id
            limit 3
            for share
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("key", normalizedKey);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                matches.Add(new RuntimeUserLinkEntity(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }

        var exact = matches.FirstOrDefault(match => match.Id.Equals(normalizedKey, StringComparison.Ordinal));
        if (exact is not null)
        {
            EnsureIdentifier(exact.Id);
            EnsureIdentifier(exact.TableName);
            return exact;
        }

        if (matches.Count != 1)
        {
            throw new RuntimeUserValidationException(
                matches.Count == 0
                    ? $"Generated entity '{normalizedKey}' was not found."
                    : $"Generated entity key '{normalizedKey}' is ambiguous; use its canonical entity id.");
        }

        EnsureIdentifier(matches[0].Id);
        EnsureIdentifier(matches[0].TableName);
        return matches[0];
    }

    private static async Task EnsureActiveRecordExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeUserLinkEntity entity,
        long recordId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select 1 from \"app\".{Quote(entity.TableName)} where \"id\" = @recordId and \"archived_at\" is null for share",
            connection,
            transaction);
        command.Parameters.AddWithValue("recordId", recordId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new RuntimeUserValidationException(
                $"Active '{entity.Id}' record '{recordId}' was not found.");
        }
    }

    private static async Task AcquireEmployeeLinkLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string entityId,
        long recordId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", $"runtime-user-employee-link:{entityId}:{recordId}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureEmployeeRecordIsAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string entityId,
        long userId,
        long recordId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select runtime_user_id
            from _runtime.runtime_user_links
            where record_id = @recordId
              and (entity_key = @entityId or lower(entity_key) in ('employee', 'employees'))
            order by id
            for update
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("recordId", recordId);
        command.Parameters.AddWithValue("entityId", entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt64(0) != userId)
            {
                throw new RuntimeUserValidationException(
                    $"Employee record '{recordId}' is already linked to another runtime user.");
            }
        }
    }

    private static async Task<RuntimeUserRecordLink?> LoadUserRecordLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select entity_key, record_id, created_at, created_by
            from _runtime.runtime_user_links
            where runtime_user_id = @userId and entity_key = @entityId
            limit 1
            for update
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("entityId", entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecordLink(userId, reader) : null;
    }

    private static RuntimeUserRecordLink ReadRecordLink(long userId, NpgsqlDataReader reader) => new(
        userId,
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetFieldValue<DateTimeOffset>(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3));

    private static bool IsEmployeeEntity(RuntimeUserLinkEntity entity) =>
        entity.Id.Equals("employee", StringComparison.OrdinalIgnoreCase) ||
        entity.Name.Equals("employees", StringComparison.OrdinalIgnoreCase) ||
        entity.TableName.Equals("employees", StringComparison.OrdinalIgnoreCase);

    private static void EnsurePositiveId(long value, string label)
    {
        if (value <= 0)
        {
            throw new RuntimeUserValidationException($"{label} must be a positive integer.");
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<RuntimeUserRow?> LoadUserAsync(
        NpgsqlConnection connection,
        long userId,
        CancellationToken cancellationToken) =>
        await LoadUserAsync(connection, null, userId, cancellationToken);

    private static async Task<RuntimeUserRow?> LoadUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, email, display_name, role, status, activated_at, last_login_at, created_at
            from _runtime.runtime_users
            where id = @id
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUserRow(reader) : null;
    }

    private static async Task<RuntimeUserRow?> LoadUserByEmailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string email,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, email, display_name, role, status, activated_at, last_login_at, created_at
            from _runtime.runtime_users
            where email = @email
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("email", email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUserRow(reader) : null;
    }

    private static RuntimeUserRow ReadUserRow(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7));

    private static async Task<IReadOnlyList<string>> LoadUserRolesAsync(
        NpgsqlConnection connection,
        long userId,
        string fallbackRole,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        var roles = new List<string>();
        await using var command = new NpgsqlCommand(
            """
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
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(reader.GetString(0).ToLowerInvariant());
        }

        if (roles.Count == 0 && !string.IsNullOrWhiteSpace(fallbackRole))
        {
            roles.Add(fallbackRole.ToLowerInvariant());
        }

        return roles;
    }

    private static async Task<RuntimeUserEmployeeLink?> LoadUserLinkAsync(
        NpgsqlConnection connection,
        long userId,
        CancellationToken cancellationToken)
    {
        var display = await LoadEmployeeDisplayAsync(connection, cancellationToken);
        if (display is null)
        {
            await using var linkCommand = connection.CreateCommand();
            linkCommand.CommandText = """
                select entity_key, record_id
                from _runtime.runtime_user_links
                where runtime_user_id = @userId
                  and lower(entity_key) in ('employee', 'employees')
                order by id desc
                limit 1
                """;
            linkCommand.Parameters.AddWithValue("userId", userId);
            await using var linkReader = await linkCommand.ExecuteReaderAsync(cancellationToken);
            return await linkReader.ReadAsync(cancellationToken)
                ? new RuntimeUserEmployeeLink(
                    linkReader.GetString(0),
                    linkReader.GetInt64(1),
                    $"{linkReader.GetString(0)} #{linkReader.GetInt64(1)}")
                : null;
        }

        var labelExpression = $"nullif({Quote(display.PrimaryColumn)}::text, '')";
        var secondaryExpression = display.SecondaryColumn is null
            ? "null"
            : $"nullif({Quote(display.SecondaryColumn)}::text, '')";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select l.entity_key, l.record_id,
                   {labelExpression} as employee_name,
                   {secondaryExpression} as employee_email
            from _runtime.runtime_user_links l
            left join app.{Quote(display.TableName)} e on l.entity_key in ('employee', 'employees') and e.id = l.record_id
            where l.runtime_user_id = @userId
              and lower(l.entity_key) in ('employee', 'employees')
            order by l.id desc
            limit 1
            """;
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var label = reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2))
            ? $"{reader.GetString(0)} #{reader.GetInt64(1)}"
            : $"{reader.GetString(2)} ({reader.GetString(3)})";
        return new RuntimeUserEmployeeLink(reader.GetString(0), reader.GetInt64(1), label);
    }

    private static async Task<RuntimeUserEmployeeDisplay?> LoadEmployeeDisplayAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        string? entityId = null;
        string? tableName = null;
        string? primary = null;
        string? secondary = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select id, table_name, metadata
                from _runtime.entity_definitions
                where id = 'employee' or name = 'employees' or table_name = 'employees'
                order by case when id = 'employee' then 0 when name = 'employees' then 1 else 2 end, id
                limit 2
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            entityId = reader.GetString(0);
            tableName = reader.GetString(1);
            var metadataJson = reader.GetString(2);
            if (!entityId.Equals("employee", StringComparison.Ordinal) &&
                await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var metadata = JsonDocument.Parse(metadataJson).RootElement;
            if (metadata.TryGetProperty("Display", out var display) || metadata.TryGetProperty("display", out display))
            {
                primary = ReadString(display, "PrimaryField") ?? ReadString(display, "primaryField");
                secondary = ReadString(display, "SecondaryField") ?? ReadString(display, "secondaryField");
            }
        }

        var fields = new List<RuntimeUserEmployeeField>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select name, column_name, field_type
                from _runtime.field_definitions
                where entity_id = @entityId
                order by name
                """;
            command.Parameters.AddWithValue("entityId", entityId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                fields.Add(new RuntimeUserEmployeeField(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (fields.Count == 0)
        {
            return null;
        }

        var primaryField = ResolveEmployeeDisplayField(fields, primary) ??
                           ResolveEmployeeDisplayField(fields, "full_name") ??
                           ResolveEmployeeDisplayField(fields, "name") ??
                           ResolveEmployeeDisplayField(fields, "email") ??
                           fields.FirstOrDefault(IsTextLikeEmployeeField);
        if (primaryField is null)
        {
            return null;
        }

        var secondaryField = ResolveEmployeeDisplayField(fields, secondary);
        if (secondaryField is null || secondaryField.ColumnName.Equals(primaryField.ColumnName, StringComparison.OrdinalIgnoreCase))
        {
            secondaryField = fields.FirstOrDefault(field =>
                field.ColumnName.Equals("email", StringComparison.OrdinalIgnoreCase) &&
                !field.ColumnName.Equals(primaryField.ColumnName, StringComparison.OrdinalIgnoreCase));
        }

        return new RuntimeUserEmployeeDisplay(tableName, primaryField.ColumnName, secondaryField?.ColumnName);
    }

    private static RuntimeUserEmployeeField? ResolveEmployeeDisplayField(
        IReadOnlyList<RuntimeUserEmployeeField> fields,
        string? displayField)
    {
        if (string.IsNullOrWhiteSpace(displayField) || displayField.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fields.FirstOrDefault(field =>
            field.Name.Equals(displayField, StringComparison.OrdinalIgnoreCase) ||
            field.ColumnName.Equals(displayField, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTextLikeEmployeeField(RuntimeUserEmployeeField field) =>
        field.FieldType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
        field.FieldType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        field.FieldType.Equals("enum", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void EnsureIdentifier(string value)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw new RuntimeUserValidationException($"Generated identifier '{value}' is invalid.");
        }
    }

    private static string Quote(string value)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw new InvalidOperationException($"Unsafe generated identifier '{value}'.");
        }

        return $"\"{value}\"";
    }

    private static async Task EnsureRolesExistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken)
    {
        foreach (var role in roles)
        {
            await using var command = new NpgsqlCommand(
                "select 1 from _runtime.runtime_roles where id = @role limit 1",
                connection,
                transaction);
            command.Parameters.AddWithValue("role", role);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new RuntimeUserValidationException($"Role '{role}' was not found.");
            }
        }
    }

    private static async Task ReplaceRolesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken)
    {
        await using (var delete = new NpgsqlCommand("delete from _runtime.runtime_user_roles where user_id = @userId", connection, transaction))
        {
            delete.Parameters.AddWithValue("userId", userId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var role in roles)
        {
            await using var insert = new NpgsqlCommand(
                "insert into _runtime.runtime_user_roles (user_id, role_id) values (@userId, @role) on conflict do nothing",
                connection,
                transaction);
            insert.Parameters.AddWithValue("userId", userId);
            insert.Parameters.AddWithValue("role", role);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string> InsertActivationTokenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        string email,
        long createdBy,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var token = GenerateToken();
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.activation_tokens (token_hash, runtime_user_id, email, expires_at, created_by)
            values (@hash, @userId, @email, @expiresAt, @createdBy)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("hash", Sha256(token));
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("expiresAt", DateTimeOffset.UtcNow.Add(lifetime));
        command.Parameters.AddWithValue("createdBy", createdBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return token;
    }

    private static async Task WriteAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext? user,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        var envelope = new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["tenantStableId"] = tenant.TenantStableId,
            ["tenantSlug"] = tenant.TenantSlug,
            ["host"] = tenant.Host,
            ["environment"] = tenant.Environment.ToString(),
            ["runtimeUserId"] = user?.UserId,
            ["runtimeUserEmail"] = user?.Email,
            ["runtimeUserRole"] = user?.Role,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        await using var command = new NpgsqlCommand(
            """
            insert into audit.events (event_type, message, metadata)
            values (@eventType, @message, @metadata)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.Add("metadata", NpgsqlTypes.NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(envelope);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<string> NormalizeRoles(IReadOnlyList<string>? roles)
    {
        var normalized = (roles ?? [])
            .Select(NormalizeAssignableRole)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new RuntimeUserValidationException("Select at least one explicit runtime role.");
        }

        return normalized;
    }

    private static string NormalizeRole(string value) => value.Trim().ToLowerInvariant();

    private static string NormalizeAssignableRole(string value)
    {
        var normalized = NormalizeRole(value);
        if (RuntimeSystemRoleIds.IsReserved(normalized))
        {
            throw new RuntimeUserValidationException(
                $"Role '{RuntimeSystemRoleIds.TenantOwner}' is reserved for runtime owner activation and cannot be assigned or removed through runtime user management.");
        }

        return normalized;
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            throw new RuntimeUserValidationException("A valid email address is required.");
        }

        return email.Trim().ToLowerInvariant();
    }

    private static string SelectPrimaryRole(IReadOnlyList<string> roles, string fallbackRole)
    {
        foreach (var candidate in new[] { RuntimeSystemRoleIds.TenantOwner, "owner", "admin", "member", "readonly" })
        {
            if (roles.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return roles.FirstOrDefault() ?? fallbackRole.ToLowerInvariant();
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record RuntimeUserRow(
        long Id,
        string Email,
        string DisplayName,
        string Role,
        string Status,
        DateTimeOffset? ActivatedAt,
        DateTimeOffset? LastLoginAt,
        DateTimeOffset CreatedAt);

    private sealed record RuntimeUserEmployeeDisplay(
        string TableName,
        string PrimaryColumn,
        string? SecondaryColumn);

    private sealed record RuntimeUserEmployeeField(
        string Name,
        string ColumnName,
        string FieldType);

    private sealed record RuntimeUserLinkEntity(
        string Id,
        string Name,
        string TableName);

    private sealed record RuntimeOwnedAcceptanceActor(
        string Email,
        string Role);
}

public sealed record RuntimeManagedUser(
    long Id,
    string Email,
    string DisplayName,
    string Status,
    IReadOnlyList<string> Roles,
    RuntimeUserEmployeeLink? LinkedEmployee,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

public sealed record RuntimeRoleOption(
    string Id,
    string DisplayName);

public sealed record RuntimeUserEmployeeLink(
    string EntityKey,
    long RecordId,
    string Label);

public sealed record RuntimeUserInviteResult(
    long UserId,
    string? ActivationToken);

public sealed record RuntimeUserRecordLink(
    long RuntimeUserId,
    string EntityId,
    long RecordId,
    DateTimeOffset CreatedAt,
    long? CreatedBy);

public sealed class RuntimeUserValidationException(string message) : Exception(message);

public sealed class RuntimeUserPermissionException(string message) : Exception(message);
