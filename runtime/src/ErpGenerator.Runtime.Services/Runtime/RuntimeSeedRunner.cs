using System.Globalization;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed class RuntimeSeedRunner(RuntimeRecordStore records) : IRuntimeSeedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeSeedResult> ApplySeedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        BundleSeedDocument seed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seed.Seed.Key))
        {
            throw new RuntimeSeedException("Seed key is required.");
        }

        var entities = await records.ListEntitiesAsync(tenant, cancellationToken);
        var refs = new Dictionary<string, RuntimeSeedRecordRef>(StringComparer.OrdinalIgnoreCase);
        var created = new List<RuntimeSeedRecordRef>();

        foreach (var record in seed.Records)
        {
            if (string.IsNullOrWhiteSpace(record.Ref))
            {
                throw new RuntimeSeedException($"Seed '{seed.Seed.Key}' contains a record without ref.");
            }

            if (refs.ContainsKey(record.Ref))
            {
                throw new RuntimeSeedException($"Seed '{seed.Seed.Key}' contains duplicate ref '{record.Ref}'.");
            }

            var entity = ResolveEntity(entities, record.Entity)
                         ?? throw new RuntimeSeedException($"Seed ref '{record.Ref}' references unknown entity '{record.Entity}'.");
            var values = ResolveRecordValues(record.Ref, entity, record.Values, refs);
            Dictionary<string, object?> createdRecord;
            try
            {
                createdRecord = await records.CreateRecordAsync(tenant, user, entity.Key, values, cancellationToken);
            }
            catch (RuntimeRecordValidationException exception)
            {
                throw new RuntimeSeedRecordValidationException(
                    record.Ref,
                    record.Entity,
                    exception.Errors.Select(error => error.Field));
            }

            var createdRef = new RuntimeSeedRecordRef(record.Ref, entity.Id, entity.Key, Convert.ToInt64(createdRecord["id"], CultureInfo.InvariantCulture));
            refs[record.Ref] = createdRef;
            created.Add(createdRef);
        }

        await records.RecordAuditEventAsync(
            tenant,
            user,
            "seed.applied",
            $"Applied seed {seed.Seed.Key}.",
            new Dictionary<string, object?>
            {
                ["seed"] = seed.Seed.Key,
                ["label"] = seed.Seed.Label,
                ["records"] = created.Select(record => new { record.Ref, record.Entity, record.Id }).ToArray()
            },
            cancellationToken);

        return new RuntimeSeedResult(seed.Seed.Key, seed.Seed.Label, created, refs);
    }

    internal static RuntimeEntityDefinition? ResolveEntity(
        IReadOnlyList<RuntimeEntityDefinition> entities,
        string entityKey)
    {
        var normalizedKey = entityKey.Trim();
        var exact = entities.FirstOrDefault(entity =>
            entity.Id.Equals(normalizedKey, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        var aliases = entities
            .Where(entity =>
                entity.Name.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase) ||
                entity.Key.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase) ||
                entity.TableName.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    internal static RuntimeFieldDefinition? ResolveField(RuntimeEntityDefinition entity, string fieldKey) =>
        entity.Fields.FirstOrDefault(field =>
            field.Id.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            field.Id.Equals($"{entity.Id}.{fieldKey}", StringComparison.OrdinalIgnoreCase) ||
            field.Id.EndsWith($".{fieldKey}", StringComparison.OrdinalIgnoreCase) ||
            field.Name.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            field.ColumnName.Equals(fieldKey, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyDictionary<string, JsonElement> ResolveRecordValues(
        string refName,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, RuntimeSeedRecordRef> refs) =>
        ResolveRecordValues(refName, entity, values, refs, allowLifecycleUpdates: false);

    internal static IReadOnlyDictionary<string, JsonElement> ResolveRecordUpdateValues(
        string refName,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, RuntimeSeedRecordRef> refs) =>
        ResolveRecordValues(refName, entity, values, refs, allowLifecycleUpdates: true);

    private static IReadOnlyDictionary<string, JsonElement> ResolveRecordValues(
        string refName,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, RuntimeSeedRecordRef> refs,
        bool allowLifecycleUpdates)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, rawValue) in values)
        {
            var field = ResolveField(entity, key)
                        ?? throw new RuntimeSeedException($"Seed ref '{refName}' references unknown field '{key}' on entity '{entity.Id}'.");
            if (field.Computed is not null)
            {
                throw new RuntimeSeedException($"Seed ref '{refName}' cannot set computed field '{key}' on entity '{entity.Id}'.");
            }

            if (field.Lifecycle is not null && !allowLifecycleUpdates)
            {
                throw new RuntimeSeedException(
                    $"Seed ref '{refName}' cannot set server-initialized lifecycle field '{key}' on entity '{entity.Id}'.");
            }

            var value = ResolveValue(refName, field, rawValue, refs);
            result[field.Name] = JsonSerializer.SerializeToElement(value, JsonOptions);
        }

        return result;
    }

    internal static object? ResolveValue(
        string refName,
        RuntimeFieldDefinition field,
        object? rawValue,
        IReadOnlyDictionary<string, RuntimeSeedRecordRef> refs)
    {
        var normalized = NormalizeYamlValue(rawValue);
        if (normalized is IReadOnlyDictionary<string, object?> map && map.TryGetValue("ref", out var referenceValue))
        {
            var reference = Convert.ToString(referenceValue, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(reference) || !refs.TryGetValue(reference, out var target))
            {
                throw new RuntimeSeedException($"Seed ref '{refName}' field '{field.Id}' references unknown record ref '{reference}'.");
            }

            if (!field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeSeedException($"Seed ref '{refName}' field '{field.Id}' uses ref on a non-relation field.");
            }

            if (!string.IsNullOrWhiteSpace(field.ReferencesEntity) &&
                !field.ReferencesEntity.Equals(target.Entity, StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeSeedException($"Seed ref '{refName}' field '{field.Id}' expects '{field.ReferencesEntity}' but ref '{reference}' is '{target.Entity}'.");
            }

            return target.Id;
        }

        return normalized;
    }

    internal static object? NormalizeYamlValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IDictionary<object, object?> objectMap)
        {
            return objectMap.ToDictionary(
                pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? string.Empty,
                pair => NormalizeYamlValue(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        if (value is IDictionary<string, object?> stringMap)
        {
            return stringMap.ToDictionary(
                pair => pair.Key,
                pair => NormalizeYamlValue(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        if (value is not string && value is IEnumerable<object?> list)
        {
            return list.Select(NormalizeYamlValue).ToArray();
        }

        return value;
    }
}

public sealed record RuntimeSeedResult(
    string Key,
    string Label,
    IReadOnlyList<RuntimeSeedRecordRef> Created,
    IReadOnlyDictionary<string, RuntimeSeedRecordRef> Refs);

public sealed record RuntimeSeedRecordRef(
    string Ref,
    string Entity,
    string EntityKey,
    long Id);

public sealed class RuntimeSeedException(string message) : Exception(message);

public sealed class RuntimeSeedRecordValidationException : Exception
{
    private const int MaximumIdentifierLength = 128;
    private const int MaximumFields = 16;

    public RuntimeSeedRecordValidationException(
        string seedRef,
        string entity,
        IEnumerable<string> fields)
        : base("Runtime seed record validation failed.")
    {
        SeedRef = BoundedIdentifier(seedRef, "unknown-seed-ref");
        Entity = BoundedIdentifier(entity, "unknown-entity");
        Fields = fields
            .Select(field => BoundedIdentifier(field, string.Empty))
            .Where(field => field.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumFields)
            .ToArray();
    }

    public string SeedRef { get; }

    public string Entity { get; }

    public IReadOnlyList<string> Fields { get; }

    private static string BoundedIdentifier(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return fallback;
        }

        var safe = new string(normalized
            .Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':' ? character : '_')
            .ToArray());
        return safe.Length <= MaximumIdentifierLength
            ? safe
            : safe[..MaximumIdentifierLength];
    }
}
