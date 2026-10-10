using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed record RuntimeEntityDefinition(
    string Key,
    string Id,
    string Name,
    string DisplayName,
    string SchemaName,
    string TableName,
    string DisplayPrimaryField,
    string? DisplaySecondaryField,
    IReadOnlyList<RuntimeFieldDefinition> Fields)
{
    public IReadOnlyList<RuntimeRelationCreateGuard> RelationCreateGuards { get; init; } = [];

    public IReadOnlyList<RuntimeRelationshipDefinition> Relationships { get; init; } = [];

    public IReadOnlyList<RuntimeEntityCollectionDefinition> Collections { get; init; } = [];
}

public sealed record RuntimeRelationshipDefinition(
    string Key,
    string SourceEntity,
    string Field,
    string TargetEntity,
    bool Required,
    string Cardinality,
    string? Inverse,
    string ArchiveBehavior);

public sealed record RuntimeEntityCollectionDefinition(
    string Key,
    string Kind,
    string Label,
    string ChildEntity,
    string ParentRelation,
    string? OrderField,
    IReadOnlyList<string> ShownFields,
    IReadOnlyList<string> EditableFields,
    IReadOnlyList<string> ActionKeys);

public sealed record RuntimeRelationCreateGuard(
    string RelationField,
    string ReferencedEntity,
    string ReferencedField,
    string Op,
    string ValueType,
    string Value);

internal static class RuntimeRelationCreateGuardMetadata
{
    public static IReadOnlyList<RuntimeRelationCreateGuard> Read(JsonElement metadata)
    {
        var entity = metadata.Deserialize<EntityDefinition>() ??
                     throw new InvalidOperationException("Persisted entity metadata is malformed.");
        return entity.RelationCreateGuards.Select(guard => new RuntimeRelationCreateGuard(
            guard.RelationField,
            guard.ReferencedEntity,
            guard.ReferencedField,
            guard.Op,
            guard.Value.Type,
            guard.Value.Value)).ToArray();
    }
}

internal static class RuntimeEntityStructureMetadata
{
    public static (
        IReadOnlyList<RuntimeRelationshipDefinition> Relationships,
        IReadOnlyList<RuntimeEntityCollectionDefinition> Collections) Read(JsonElement metadata)
    {
        var entity = metadata.Deserialize<EntityDefinition>() ??
                     throw new InvalidOperationException("Persisted entity metadata is malformed.");
        return (
            entity.Relationships.Select(relationship => new RuntimeRelationshipDefinition(
                relationship.Key,
                relationship.SourceEntity,
                relationship.Field,
                relationship.TargetEntity,
                relationship.Required,
                relationship.Cardinality,
                relationship.Inverse,
                relationship.ArchiveBehavior)).ToArray(),
            entity.Collections.Select(collection => new RuntimeEntityCollectionDefinition(
                collection.Key,
                collection.Kind,
                collection.Label,
                collection.ChildEntity,
                collection.ParentRelation,
                collection.OrderField,
                collection.ShownFields,
                collection.EditableFields,
                collection.ActionKeys)).ToArray());
    }
}

public sealed record RuntimeFieldDefinition(
    string Id,
    string EntityId,
    string Name,
    string DisplayName,
    string FieldType,
    string ColumnName,
    string SqlType,
    bool Required,
    bool Unique,
    string? ReferencesEntity,
    IReadOnlyList<string> Options,
    RuntimeComputedField? Computed,
    RuntimeFieldLifecycle? Lifecycle = null);

public sealed record RuntimeFieldLifecycle(
    string Initial,
    string Transitions)
{
    public bool RequiresNamedAction =>
        Transitions.Equals("named_action", StringComparison.OrdinalIgnoreCase);
}

internal static class RuntimeFieldLifecycleMetadata
{
    public static RuntimeFieldLifecycle? Read(
        JsonElement metadata,
        string fieldType,
        bool required,
        bool unique,
        IReadOnlyList<string> options,
        bool computed,
        FieldDefinition authoritativeField)
    {
        RuntimeFieldLifecycle? persistedLifecycle;
        if (!TryGetProperty(metadata, "Lifecycle", out var lifecycle) ||
            lifecycle.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            persistedLifecycle = null;
        }
        else
        {
            if (lifecycle.ValueKind != JsonValueKind.Object ||
                !TryReadString(lifecycle, "Initial", out var initial) ||
                !TryReadString(lifecycle, "Transitions", out var transitions))
            {
                throw new InvalidOperationException("Persisted field lifecycle metadata is malformed.");
            }

            persistedLifecycle = Validate(
                initial,
                transitions,
                fieldType,
                required,
                unique,
                options,
                computed,
                "Persisted field lifecycle");
        }

        var authoritativeLifecycle = authoritativeField.Lifecycle is null
            ? null
            : Validate(
                authoritativeField.Lifecycle.Initial,
                authoritativeField.Lifecycle.Transitions,
                authoritativeField.Type,
                authoritativeField.Required,
                authoritativeField.Unique,
                authoritativeField.Options,
                authoritativeField.Computed is not null,
                "Active compiled field lifecycle");
        if (persistedLifecycle is null && authoritativeLifecycle is null)
        {
            return null;
        }

        if (persistedLifecycle is null ||
            authoritativeLifecycle is null ||
            !persistedLifecycle.Initial.Equals(authoritativeLifecycle.Initial, StringComparison.Ordinal) ||
            !persistedLifecycle.Transitions.Equals(authoritativeLifecycle.Transitions, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Persisted field lifecycle metadata for '{authoritativeField.Id}' does not match the active compiled bundle definition.");
        }

        return persistedLifecycle;
    }

    private static RuntimeFieldLifecycle Validate(
        string initial,
        string transitions,
        string fieldType,
        bool required,
        bool unique,
        IReadOnlyList<string> options,
        bool computed,
        string source)
    {
        if (!fieldType.Equals("enum", StringComparison.OrdinalIgnoreCase) ||
            !required ||
            unique ||
            computed)
        {
            throw new InvalidOperationException(
                $"{source} must belong to a required, nonunique, noncomputed enum field.");
        }

        var canonicalInitial = options.FirstOrDefault(option =>
            option.Equals(initial, StringComparison.OrdinalIgnoreCase));
        if (canonicalInitial is null)
        {
            throw new InvalidOperationException(
                $"{source} initial value must match a declared enum option.");
        }

        var normalizedTransitions = transitions.Trim().ToLowerInvariant();
        if (normalizedTransitions is not "direct" and not "named_action")
        {
            throw new InvalidOperationException(
                $"{source} transitions must be direct or named_action.");
        }

        return new RuntimeFieldLifecycle(canonicalInitial, normalizedTransitions);
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        if (TryGetProperty(element, propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value) ||
            element.TryGetProperty(char.ToLowerInvariant(propertyName[0]) + propertyName[1..], out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}

internal static class RuntimeAuthoritativeFieldDefinitions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyDictionary<string, FieldDefinition>> LoadAsync(
        NpgsqlConnection connection,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select coalesce(bv.compiled_metadata, bv.metadata)::text
            from _runtime.active_bundle ab
            join _runtime.bundle_versions bv on bv.id = ab.bundle_version_id
            where ab.id = true
            limit 1
            """;
        var metadata = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(metadata))
        {
            throw new InvalidOperationException(
                "Active compiled bundle metadata is required to authorize runtime field lifecycle policy.");
        }

        CompiledBundle compiled;
        try
        {
            compiled = JsonSerializer.Deserialize<CompiledBundle>(metadata, JsonOptions)
                       ?? throw new JsonException("Compiled bundle was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Active compiled bundle metadata could not be read for runtime field lifecycle policy.",
                exception);
        }

        var entities = compiled.Entities
            .Where(entity => entity.Id.Equals(entityId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (entities.Length != 1)
        {
            throw new InvalidOperationException(
                $"Active compiled bundle must contain exactly one entity definition for '{entityId}'.");
        }

        var result = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var field in entities[0].Fields)
        {
            var key = $"{entityId}.{field.Id}";
            if (!result.TryAdd(key, field))
            {
                throw new InvalidOperationException(
                    $"Active compiled bundle contains duplicate field definition '{key}'.");
            }
        }

        return result;
    }

    public static FieldDefinition GetRequired(
        IReadOnlyDictionary<string, FieldDefinition> authoritativeFields,
        string persistedFieldId)
    {
        if (!authoritativeFields.TryGetValue(persistedFieldId, out var field))
        {
            throw new InvalidOperationException(
                $"Persisted field '{persistedFieldId}' is not present in the active compiled bundle definition.");
        }

        return field;
    }
}

public sealed record RuntimeComputedField(
    string Op,
    RuntimeComputedOperand Left,
    RuntimeComputedOperand Right);

public sealed record RuntimeComputedOperand(
    string? Field,
    object? Value);

public sealed record RuntimeRecordQuery(
    int Page = 1,
    int PageSize = 25,
    string? SortBy = null,
    string? SortDirection = null,
    string? Search = null,
    bool IncludeArchived = false);

public sealed record RuntimeRecordList(
    RuntimeEntityDefinition Entity,
    IReadOnlyList<Dictionary<string, object?>> Records,
    int Page,
    int PageSize,
    long Total);

public sealed record RuntimeAuditEvent(
    long Id,
    string EventType,
    string Message,
    JsonElement Metadata,
    DateTimeOffset CreatedAt);

public sealed record RuntimeLookupItem(
    long Id,
    string Label,
    string? SecondaryLabel);

internal sealed record RuntimeEntityDisplay(
    string? PrimaryField,
    string? SecondaryField);

public sealed record RuntimeUserContext(
    long UserId,
    string Email,
    string Role)
{
    public IReadOnlyList<string> Roles { get; init; } =
        string.IsNullOrWhiteSpace(Role) ? [] : [Role.Trim().ToLowerInvariant()];

    public bool HasRole(string role) =>
        Roles.Any(candidate => candidate.Equals(role, StringComparison.OrdinalIgnoreCase));
}

public sealed record RuntimeValidationError(
    string Field,
    string Message);

public class RuntimeRecordValidationException(IReadOnlyList<RuntimeValidationError> errors)
    : Exception("Runtime record validation failed.")
{
    public IReadOnlyList<RuntimeValidationError> Errors { get; } = errors;
}

public sealed class RuntimeLifecycleTransitionDeniedException(
    string entityKey,
    string field,
    string displayName)
    : RuntimeRecordValidationException(
        [new RuntimeValidationError(field, $"{displayName} can be changed only by an authorized named action.")])
{
    public string EntityKey { get; } = entityKey;

    public string Field { get; } = field;
}

public sealed class RuntimeRelationCreateGuardRejectedException(
    string entityKey,
    string relationField,
    string referencedEntity,
    string referencedField)
    : Exception($"Relation create guard rejected creation of '{entityKey}' through '{relationField}'.")
{
    public const string StableCode = "relation.create_guard";

    public string Code => StableCode;

    public string EntityKey { get; } = entityKey;

    public string RelationField { get; } = relationField;

    public string ReferencedEntity { get; } = referencedEntity;

    public string ReferencedField { get; } = referencedField;
}

public sealed class RuntimeEntityNotFoundException(string entityKey)
    : Exception($"Runtime entity '{entityKey}' was not found.")
{
    public string EntityKey { get; } = entityKey;
}

public sealed class RuntimeRecordPermissionException(string action, string entityKey)
    : Exception($"Runtime record action '{action}' is forbidden for entity '{entityKey}'.")
{
    public string Action { get; } = action;

    public string EntityKey { get; } = entityKey;
}
