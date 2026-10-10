using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeRecordStore : IRuntimeRecordService
{
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private readonly ITenantDatabaseConnectionFactory connectionFactory;
    private readonly RuntimePermissionService permissions;
    private static readonly RuntimeUserContext SystemOwner = new(
        long.MinValue,
        "runtime-record-store@system.invalid",
        ErpGenerator.Runtime.Shared.Compilation.RuntimeSystemRoleIds.TenantOwner)
    {
        Roles = [ErpGenerator.Runtime.Shared.Compilation.RuntimeSystemRoleIds.TenantOwner]
    };

    public RuntimeRecordStore(ITenantDatabaseConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
        permissions = new RuntimePermissionService(connectionFactory);
    }

    public async Task<IReadOnlyList<RuntimeEntityDefinition>> ListEntitiesAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entities = new List<RuntimeEntityDefinition>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, name, display_name, table_name, metadata
            from _runtime.entity_definitions
            order by display_name
            """;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var metadata = JsonDocument.Parse(reader.GetString(4)).RootElement;
                var display = ReadDisplay(metadata);
                var structure = RuntimeEntityStructureMetadata.Read(metadata);
                entities.Add(new RuntimeEntityDefinition(
                    reader.GetString(3),
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    "app",
                    reader.GetString(3),
                    display.PrimaryField ?? string.Empty,
                    display.SecondaryField,
                    [])
                {
                    RelationCreateGuards = RuntimeRelationCreateGuardMetadata.Read(metadata),
                    Relationships = structure.Relationships,
                    Collections = structure.Collections
                });
            }
        }

        var result = new List<RuntimeEntityDefinition>();
        foreach (var entity in entities)
        {
            var fields = await LoadFieldsAsync(connection, entity.Id, cancellationToken);
            result.Add(ApplyDisplayDefaults(entity with
            {
                Fields = fields
            }));
        }

        return result;
    }

    public async Task<RuntimeEntityDefinition?> GetEntityAsync(
        ResolvedTenantEnvironment tenant,
        string entityKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        return await LoadEntityAsync(connection, entityKey, cancellationToken);
    }

    internal Task<RuntimeRecordList> ListRecordsAsync(
        ResolvedTenantEnvironment tenant,
        string entityKey,
        RuntimeRecordQuery query,
        CancellationToken cancellationToken = default) =>
        ListRecordsAsync(tenant, SystemOwner, entityKey, query, cancellationToken);

    public async Task<RuntimeRecordList> ListRecordsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        RuntimeRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var snapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var readableEntity = permissions.FilterReadableEntity(snapshot, entity);
        var recordPredicate = await BuildReadPredicateAsync(
            connection,
            transaction: null,
            snapshot,
            user,
            entity,
            "r",
            "record_read",
            query.IncludeArchived,
            cancellationToken);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? DefaultPageSize : query.PageSize, 1, MaxPageSize);
        var offset = (page - 1) * pageSize;
        var where = BuildListWhere(
            readableEntity,
            "r",
            query.Search,
            query.IncludeArchived,
            recordPredicate,
            out var searchParameter);
        var sortColumn = ResolveSortColumn(readableEntity, query.SortBy);
        var sortDirection = string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase)
            ? "asc"
            : "desc";

        var records = new List<Dictionary<string, object?>>();
        var selectColumns = BuildSelectColumns(entity);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                select {selectColumns}
                from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as "r"
                {where.Sql}
                order by {Quote(sortColumn)} {sortDirection}
                limit @limit offset @offset
                """;
            command.Parameters.AddWithValue("limit", pageSize);
            command.Parameters.AddWithValue("offset", offset);
            if (searchParameter is not null)
            {
                command.Parameters.AddWithValue("q", searchParameter);
            }
            recordPredicate.AddParameters(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(ReadRecord(reader));
            }
        }

        long total;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                select count(*)
                from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as "r"
                {where.Sql}
                """;
            if (searchParameter is not null)
            {
                command.Parameters.AddWithValue("q", searchParameter);
            }
            recordPredicate.AddParameters(command);

            total = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await HydrateRelationLabelsAsync(connection, snapshot, user, entity, records, cancellationToken);
        ApplyComputedFields(entity, records);
        return new RuntimeRecordList(
            readableEntity,
            records.Select(record => RuntimePermissionService.FilterReadableRecord(entity, readableEntity, record)).ToArray(),
            page,
            pageSize,
            total);
    }

    internal Task<Dictionary<string, object?>?> GetRecordAsync(
        ResolvedTenantEnvironment tenant,
        string entityKey,
        long recordId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default) =>
        GetRecordAsync(tenant, SystemOwner, entityKey, recordId, includeArchived, cancellationToken);

    public async Task<Dictionary<string, object?>?> GetRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var snapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var predicate = await BuildReadPredicateAsync(
            connection,
            transaction: null,
            snapshot,
            user,
            entity,
            "r",
            "record_get",
            includeArchived,
            cancellationToken);

        var record = await ReadRecordByIdAsync(
            connection,
            entity,
            recordId,
            cancellationToken,
            includeArchived: includeArchived,
            predicate: predicate);
        if (record is not null)
        {
            await HydrateRelationLabelsAsync(connection, snapshot, user, entity, [record], cancellationToken);
            ApplyComputedFields(entity, [record]);
            record = RuntimePermissionService.FilterReadableRecord(
                entity,
                permissions.FilterReadableEntity(snapshot, entity),
                record);
        }

        return record;
    }

    public async Task<Dictionary<string, object?>> CreateRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var initialSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        EnsureEntityActionAllowed(initialSnapshot, "create", entity);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await permissions.CreateSnapshotAsync(connection, transaction, user, cancellationToken);
        EnsureEntityActionAllowed(snapshot, "create", entity);
        var converted = await ValidateInputAsync(
            connection,
            transaction,
            snapshot,
            user,
            entity,
            values,
            isCreate: true,
            cancellationToken);
        converted["created_by"] = user.UserId;
        converted["updated_by"] = user.UserId;
        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                snapshot,
                user,
                "create",
                entity,
                converted,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeRecordPermissionException("create", entity.Key);
        }

        await RuntimeRelationCreateGuardEnforcer.EnsureAllowedAsync(
            connection,
            transaction,
            entity,
            converted,
            (guardedRelations, token) => EnsureGuardedRelationAccessAsync(
                connection,
                transaction,
                snapshot,
                user,
                guardedRelations,
                token),
            cancellationToken);

        var columns = converted.Keys.ToArray();
        Dictionary<string, object?> record;
        await using (var command = new NpgsqlCommand { Connection = connection, Transaction = transaction })
        {
            if (columns.Length == 0)
            {
                command.CommandText = $"""
                    insert into {Quote(entity.SchemaName)}.{Quote(entity.TableName)} default values
                    returning {BuildSelectColumns(entity)}
                    """;
            }
            else
            {
                var parameterNames = columns.Select((_, index) => $"@p{index}").ToArray();
                command.CommandText = $"""
                    insert into {Quote(entity.SchemaName)}.{Quote(entity.TableName)} ({string.Join(", ", columns.Select(Quote))})
                    values ({string.Join(", ", parameterNames)})
                    returning {BuildSelectColumns(entity)}
                    """;

                for (var index = 0; index < columns.Length; index++)
                {
                    command.Parameters.AddWithValue($"p{index}", converted[columns[index]] ?? DBNull.Value);
                }
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Record insert did not return a row.");
            }

            record = ReadRecord(reader);
        }

        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                snapshot,
                user,
                "create",
                entity,
                record,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeRecordPermissionException("create", entity.Key);
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "record.created",
            $"Created {entity.DisplayName} record.",
            new Dictionary<string, object?>
            {
                ["entity"] = entity.Key,
                ["recordId"] = record["id"],
                ["fields"] = columns
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        if (!await IsMutationOutputReadableAsync(
                connection,
                snapshot,
                user,
                entity,
                record,
                cancellationToken))
        {
            return MinimalMutationOutput(record);
        }

        await HydrateRelationLabelsAsync(connection, snapshot, user, entity, [record], cancellationToken);
        ApplyComputedFields(entity, [record]);
        return RuntimePermissionService.FilterReadableRecord(
            entity,
            permissions.FilterReadableEntity(snapshot, entity),
            record);
    }

    public async Task<Dictionary<string, object?>?> UpdateRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var initialSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        EnsureEntityActionAllowed(initialSnapshot, "update", entity);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await permissions.CreateSnapshotAsync(connection, transaction, user, cancellationToken);
        EnsureEntityActionAllowed(snapshot, "update", entity);
        var converted = await ValidateInputAsync(
            connection,
            transaction,
            snapshot,
            user,
            entity,
            values,
            isCreate: false,
            cancellationToken);
        if (converted.Count == 0)
        {
            throw new RuntimeRecordValidationException([new RuntimeValidationError("record", "At least one writable field is required.")]);
        }
        var predicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            snapshot,
            user,
            "update",
            entity,
            "r",
            "record_update",
            cancellationToken);
        var before = await ReadRecordByIdAsync(
            connection,
            entity,
            recordId,
            cancellationToken,
            transaction,
            includeArchived: false,
            predicate: predicate,
            forUpdate: true);
        if (before is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var columns = converted.Keys.ToArray();
        Dictionary<string, object?> record;
        await using (var command = new NpgsqlCommand { Connection = connection, Transaction = transaction })
        {
            var assignments = columns.Select((column, index) => $"{Quote(column)} = @p{index}").ToList();
            assignments.Add("\"updated_at\" = now()");
            assignments.Add("\"updated_by\" = @updatedBy");
            command.CommandText = $"""
                update {Quote(entity.SchemaName)}.{Quote(entity.TableName)}
                set {string.Join(", ", assignments)}
                where id = @id and archived_at is null
                returning {BuildSelectColumns(entity)}
                """;

            for (var index = 0; index < columns.Length; index++)
            {
                command.Parameters.AddWithValue($"p{index}", converted[columns[index]] ?? DBNull.Value);
            }

            command.Parameters.AddWithValue("id", recordId);
            command.Parameters.AddWithValue("updatedBy", user.UserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            record = ReadRecord(reader);
        }

        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                snapshot,
                user,
                "update",
                entity,
                record,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "record.updated",
            $"Updated {entity.DisplayName} record.",
            new Dictionary<string, object?>
            {
                ["entity"] = entity.Key,
                ["recordId"] = recordId,
                ["fields"] = columns,
                ["before"] = before,
                ["after"] = record
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        if (!await IsMutationOutputReadableAsync(
                connection,
                snapshot,
                user,
                entity,
                record,
                cancellationToken))
        {
            return MinimalMutationOutput(record);
        }

        await HydrateRelationLabelsAsync(connection, snapshot, user, entity, [record], cancellationToken);
        ApplyComputedFields(entity, [record]);
        return RuntimePermissionService.FilterReadableRecord(
            entity,
            permissions.FilterReadableEntity(snapshot, entity),
            record);
    }

    public async Task<bool> ArchiveRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        string? archiveReason = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await permissions.CreateSnapshotAsync(connection, transaction, user, cancellationToken);
        EnsureEntityActionAllowed(snapshot, "archive", entity);
        var predicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            snapshot,
            user,
            "archive",
            entity,
            "r",
            "record_archive",
            cancellationToken);
        var before = await ReadRecordByIdAsync(
            connection,
            entity,
            recordId,
            cancellationToken,
            transaction,
            includeArchived: false,
            predicate: predicate,
            forUpdate: true);
        if (before is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var command = new NpgsqlCommand(
            $"""
            update {Quote(entity.SchemaName)}.{Quote(entity.TableName)}
            set archived_at = now(),
                archived_by = @archivedBy,
                archive_reason = @archiveReason,
                updated_at = now(),
                updated_by = @archivedBy
            where id = @id and archived_at is null
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", recordId);
            command.Parameters.AddWithValue("archivedBy", user.UserId);
            command.Parameters.AddWithValue("archiveReason", (object?)archiveReason ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                snapshot,
                user,
                "archive",
                entity,
                before,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "record.archived",
            $"Archived {entity.DisplayName} record.",
            new Dictionary<string, object?>
            {
                ["entity"] = entity.Key,
                ["recordId"] = recordId,
                ["archiveReason"] = archiveReason,
                ["before"] = before
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal Task<IReadOnlyList<RuntimeLookupItem>> LookupRecordsAsync(
        ResolvedTenantEnvironment tenant,
        string entityKey,
        string? search,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        LookupRecordsAsync(tenant, SystemOwner, entityKey, search, limit, cancellationToken);

    public async Task<IReadOnlyList<RuntimeLookupItem>> LookupRecordsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        string? search,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var snapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        EnsureEntityActionAllowed(snapshot, "read", entity);
        var readableEntity = permissions.FilterReadableEntity(snapshot, entity);
        var predicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction: null,
            snapshot,
            user,
            "read",
            entity,
            "r",
            "record_lookup",
            cancellationToken);

        return await LookupRecordsAsync(connection, readableEntity, search, limit, predicate, cancellationToken);
    }

    internal Task<IReadOnlyDictionary<string, IReadOnlyList<RuntimeLookupItem>>> ListRelationOptionsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeEntityDefinition entity,
        CancellationToken cancellationToken = default) =>
        ListRelationOptionsAsync(tenant, SystemOwner, entity, cancellationToken);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<RuntimeLookupItem>>> ListRelationOptionsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var snapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var readableEntity = permissions.FilterReadableEntity(snapshot, entity);
        var options = new Dictionary<string, IReadOnlyList<RuntimeLookupItem>>(StringComparer.OrdinalIgnoreCase);
        var relationIndex = 0;
        foreach (var field in readableEntity.Fields.Where(field => field.Computed is null && field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) && field.ReferencesEntity is not null))
        {
            var target = await LoadEntityByIdAsync(connection, field.ReferencesEntity!, cancellationToken);
            if (target is null)
            {
                options[field.Name] = [];
                continue;
            }

            if (!permissions.IsAllowed(snapshot, "read", target))
            {
                options[field.Name] = [];
                continue;
            }

            var predicate = await permissions.BuildRecordPredicateAsync(
                connection,
                transaction: null,
                snapshot,
                user,
                "read",
                target,
                "r",
                $"relation_option_{relationIndex++}",
                cancellationToken);
            var readableTarget = permissions.FilterReadableEntity(snapshot, target);
            options[field.Name] = await LookupRecordsAsync(
                connection,
                readableTarget,
                null,
                50,
                predicate,
                cancellationToken);
        }

        return options;
    }

    internal Task<IReadOnlyDictionary<string, long>> CountRecordsAsync(
        ResolvedTenantEnvironment tenant,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        CancellationToken cancellationToken = default) =>
        CountRecordsAsync(
            tenant,
            SystemOwner,
            entities,
            includeArchived: false,
            cancellationToken: cancellationToken);

    internal Task<IReadOnlyDictionary<string, long>> CountRecordsAsync(
        ResolvedTenantEnvironment tenant,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        bool includeArchived,
        CancellationToken cancellationToken = default) =>
        CountRecordsAsync(tenant, SystemOwner, entities, includeArchived, cancellationToken);

    public Task<IReadOnlyDictionary<string, long>> CountRecordsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        CancellationToken cancellationToken = default) =>
        CountRecordsAsync(
            tenant,
            user,
            entities,
            includeArchived: false,
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyDictionary<string, long>> CountRecordsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var snapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entity, index) in entities.Select((entity, index) => (entity, index)))
        {
            var predicate = await BuildReadPredicateAsync(
                connection,
                transaction: null,
                snapshot,
                user,
                entity,
                "r",
                $"record_count_{index}",
                includeArchived,
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                select count(*)
                from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as "r"
                where {(includeArchived ? string.Empty : "\"r\".\"archived_at\" is null and ")}
                      ({predicate.Sql})
                """;
            predicate.AddParameters(command);
            counts[entity.Key] = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    public async Task<bool> RestoreRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await permissions.CreateSnapshotAsync(connection, transaction, user, cancellationToken);
        EnsureEntityActionAllowed(snapshot, "restore", entity);
        var predicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            snapshot,
            user,
            "restore",
            entity,
            "r",
            "record_restore",
            cancellationToken);
        var before = await ReadRecordByIdAsync(
            connection,
            entity,
            recordId,
            cancellationToken,
            transaction,
            includeArchived: true,
            predicate: predicate,
            forUpdate: true);
        if (before is null || before.GetValueOrDefault("archived_at") is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var command = new NpgsqlCommand(
            $"""
            update {Quote(entity.SchemaName)}.{Quote(entity.TableName)}
            set archived_at = null,
                archived_by = null,
                archive_reason = null,
                updated_at = now(),
                updated_by = @updatedBy
            where id = @id
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", recordId);
            command.Parameters.AddWithValue("updatedBy", user.UserId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                snapshot,
                user,
                "restore",
                entity,
                before,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "record.restored",
            $"Restored {entity.DisplayName} record.",
            new Dictionary<string, object?>
            {
                ["entity"] = entity.Key,
                ["recordId"] = recordId,
                ["before"] = before
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<RuntimeAuditEvent>> ListAuditEventsAsync(
        ResolvedTenantEnvironment tenant,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, event_type, message, metadata, created_at
            from audit.events
            order by id desc
            limit @limit
            """;
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        var events = new List<RuntimeAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new RuntimeAuditEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return events;
    }

    public async Task RecordAuditEventAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext? user,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await WriteAuditEventAsync(connection, null, tenant, user, eventType, message, metadata, cancellationToken);
    }

}
