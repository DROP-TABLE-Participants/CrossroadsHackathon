using ErpGenerator.Runtime.Shared.Bundles;
using System.Globalization;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleEntityValidator
{
    private static readonly IReadOnlySet<string> RuntimeSystemFieldIdentifiers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "created_at",
            "created_by",
            "updated_at",
            "updated_by",
            "archived_at",
            "archived_by",
            "archive_reason"
        };

    public static void ValidateIdentityNamespace(
        IEnumerable<ErpEntity> entities,
        List<string> errors)
    {
        var owners = new Dictionary<string, (string EntityId, string Surface)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            var surfaces = new (string Value, string Surface)[]
            {
                (entity.Id, "id"),
                (entity.Name, "name/generated table name"),
                ($"{entity.Id}s", "implicit plural alias")
            };

            foreach (var (value, surface) in surfaces)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = value.Trim();
                if (!owners.TryGetValue(normalized, out var owner))
                {
                    owners[normalized] = (entity.Id, surface);
                    continue;
                }

                if (!owner.EntityId.Equals(entity.Id, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"Ambiguous entity identifier '{normalized}' is owned by entity '{owner.EntityId}' " +
                        $"({owner.Surface}) and entity '{entity.Id}' ({surface}). Entity ids, names, generated table names, " +
                        "and implicit plural aliases must be unique across the bundle.");
                }
            }
        }
    }

    public static void ValidateEntity(
        ErpEntity entity,
        HashSet<string> entityIds,
        IDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        RequireIdentifier(entity.Id, $"entity[{entity.Name}].id", errors);
        RequireIdentifier(entity.Name, $"entity[{entity.Id}].name", errors);
        RequireText(entity.DisplayName, $"entity[{entity.Id}].displayName", errors);
        RejectSqlLikeContent(entity.DisplayName, $"entity[{entity.Id}].displayName", errors);

        if (!entityIds.Add(entity.Id))
        {
            errors.Add($"Duplicate entity id '{entity.Id}'.");
        }

        if (entity.Fields.Count == 0)
        {
            errors.Add($"Entity '{entity.Id}' must contain at least one field.");
        }

        var fieldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relationshipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in entity.Fields)
        {
            RequireIdentifier(field.Id, $"entity[{entity.Id}].field[{field.Name}].id", errors);
            RequireIdentifier(field.Name, $"entity[{entity.Id}].field[{field.Id}].name", errors);
            RequireText(field.DisplayName, $"entity[{entity.Id}].field[{field.Id}].displayName", errors);
            RejectSqlLikeContent(field.DisplayName, $"entity[{entity.Id}].field[{field.Id}].displayName", errors);

            if (!fieldIds.Add(field.Id))
            {
                errors.Add($"Duplicate field id '{field.Id}' in entity '{entity.Id}'.");
            }

            if (!fieldNames.Add(field.Name))
            {
                errors.Add($"Duplicate field name '{field.Name}' in entity '{entity.Id}'.");
            }

            if (!FieldTypes.Contains(field.Type))
            {
                errors.Add($"Field '{entity.Id}.{field.Id}' uses unsupported type '{field.Type}'.");
            }

            if (field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(field.References))
            {
                errors.Add($"Relation field '{entity.Id}.{field.Id}' must declare references.");
            }

            ValidateRelationship(entity, field, errors);
            if (field.Relationship is not null &&
                !string.IsNullOrWhiteSpace(field.Relationship.Key) &&
                !relationshipKeys.Add(field.Relationship.Key))
            {
                errors.Add(
                    $"Duplicate relationship key '{field.Relationship.Key}' in entity '{entity.Id}'.");
            }

            if (field.Type.Equals("enum", StringComparison.OrdinalIgnoreCase) && field.Options.Count == 0)
            {
                errors.Add($"Enum field '{entity.Id}.{field.Id}' must declare at least one option.");
            }

            foreach (var option in field.Options)
            {
                RequireText(option, $"entity[{entity.Id}].field[{field.Id}].option", errors);
                RejectSqlLikeContent(option, $"entity[{entity.Id}].field[{field.Id}].option", errors);
            }

            ValidateLifecycle(entity, field, errors);
        }

        ValidateFieldIdentityNamespace(entity, errors);
        var fieldLookup = BuildFieldLookup(entity.Fields);
        ValidateComputedFields(entity, fieldLookup, errors);
        ValidateDisplay(entity, fieldNames, errors);
        fieldsByEntity[entity.Id] = fieldLookup;
    }

    private static void ValidateLifecycle(ErpEntity entity, ErpField field, List<string> errors)
    {
        if (field.Lifecycle is null)
        {
            return;
        }

        var path = $"entity[{entity.Id}].field[{field.Id}].lifecycle";
        var initial = field.Lifecycle.Initial ?? string.Empty;
        var transitions = field.Lifecycle.Transitions ?? string.Empty;
        RequireText(initial, $"{path}.initial", errors);
        RejectSqlLikeContent(initial, $"{path}.initial", errors);
        RejectSqlLikeContent(transitions, $"{path}.transitions", errors);

        if (!field.Type.Equals("enum", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Lifecycle field '{entity.Id}.{field.Id}' must use type 'enum'.");
        }

        if (!field.Required)
        {
            errors.Add($"Lifecycle field '{entity.Id}.{field.Id}' must be required.");
        }

        if (field.Unique)
        {
            errors.Add($"Lifecycle field '{entity.Id}.{field.Id}' must not be unique.");
        }

        if (field.Computed is not null)
        {
            errors.Add($"Lifecycle field '{entity.Id}.{field.Id}' must not be computed.");
        }

        if (string.IsNullOrWhiteSpace(initial) ||
            !field.Options.Contains(initial.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Lifecycle field '{entity.Id}.{field.Id}' initial value '{field.Lifecycle.Initial}' must match one declared enum option.");
        }

        var normalizedTransitions = transitions.Trim();
        if (!FieldLifecycleTransitions.Contains(normalizedTransitions) ||
            !transitions.Equals(normalizedTransitions, StringComparison.Ordinal))
        {
            errors.Add(
                $"Lifecycle field '{entity.Id}.{field.Id}' transitions must be 'direct' or 'named_action' and cannot contain surrounding whitespace.");
        }
    }

    private static void ValidateFieldIdentityNamespace(ErpEntity entity, List<string> errors)
    {
        var owners = new Dictionary<string, (int FieldIndex, string FieldId, string Surface)>(
            StringComparer.OrdinalIgnoreCase);
        for (var fieldIndex = 0; fieldIndex < entity.Fields.Count; fieldIndex++)
        {
            var field = entity.Fields[fieldIndex];
            var surfaces = new List<(string Value, string Surface)>
            {
                (field.Id, "id"),
                (field.Name, "name/generated column")
            };
            if (field.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
            {
                surfaces.Add((field.Name[..^3], "implicit relation alias"));
            }

            foreach (var (value, surface) in surfaces
                         .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
                         .DistinctBy(candidate => candidate.Value.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var normalized = value.Trim();
                if (RuntimeSystemFieldIdentifiers.Contains(normalized))
                {
                    errors.Add(
                        $"Field '{entity.Id}.{field.Id}' {surface} '{normalized}' conflicts with a reserved runtime system field identifier. " +
                        "Bundle field ids, names, generated columns, and implicit relation aliases must not use id, created_at, created_by, " +
                        "updated_at, updated_by, archived_at, archived_by, or archive_reason.");
                }

                if (normalized.EndsWith("__label", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith("__secondaryLabel", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"Field '{entity.Id}.{field.Id}' {surface} '{normalized}' uses a reserved runtime relation-label suffix. " +
                        "Bundle field ids, names, generated columns, and implicit aliases must not end with __label or __secondaryLabel.");
                }

                if (!owners.TryGetValue(normalized, out var owner))
                {
                    owners[normalized] = (fieldIndex, field.Id, surface);
                    continue;
                }

                if (owner.FieldIndex != fieldIndex)
                {
                    errors.Add(
                        $"Ambiguous field identifier '{normalized}' in entity '{entity.Id}' is owned by field '{owner.FieldId}' " +
                        $"({owner.Surface}) and field '{field.Id}' ({surface}). Field ids, names, generated columns, and implicit relation " +
                        "aliases must be unique within an entity.");
                }
            }
        }
    }

    public static void ValidateRelationTargets(
        ErpEntity entity,
        IReadOnlySet<string> entityIds,
        List<string> errors)
    {
        foreach (var field in entity.Fields.Where(field => !string.IsNullOrWhiteSpace(field.References)))
        {
            if (!entityIds.Contains(field.References!))
            {
                errors.Add($"Field '{entity.Id}.{field.Id}' references unknown entity '{field.References}'.");
            }
        }
    }

    public static void ValidateCollections(
        ErpEntity entity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlySet<string> actionKeys,
        List<string> errors)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in entity.Collections)
        {
            var path = $"entity[{entity.Id}].collection[{collection.Key}]";
            RequireIdentifier(collection.Key, $"{path}.key", errors);
            RequireText(collection.Label, $"{path}.label", errors);
            if (!keys.Add(collection.Key))
            {
                errors.Add($"Duplicate collection key '{collection.Key}' in entity '{entity.Id}'.");
            }

            if (!collection.Kind.Equals("line_items", StringComparison.Ordinal))
            {
                errors.Add($"{path}.kind must be exactly 'line_items'.");
            }

            if (!fieldsByEntity.TryGetValue(collection.ChildEntity, out var childFields))
            {
                errors.Add($"{path}.child_entity references unknown entity '{collection.ChildEntity}'.");
                continue;
            }

            if (!childFields.TryGetValue(collection.ParentRelation, out var parentRelation) ||
                !parentRelation.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                !parentRelation.Required ||
                !string.Equals(parentRelation.References, entity.Id, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"{path}.parent_relation must be a required relation from child entity '{collection.ChildEntity}' to '{entity.Id}'.");
            }

            if (!string.IsNullOrWhiteSpace(collection.OrderField) &&
                (!childFields.TryGetValue(collection.OrderField, out var orderField) ||
                 orderField.Computed is not null ||
                 !orderField.Type.Equals("integer", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{path}.order_field must be one materialized integer field on the child entity.");
            }

            ValidateCollectionFields(path, "shown_fields", collection.ShownFields, childFields, errors);
            ValidateCollectionFields(path, "editable_fields", collection.EditableFields, childFields, errors);
            foreach (var field in collection.EditableFields)
            {
                if (!collection.ShownFields.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{path}.editable_fields field '{field}' must also appear in shown_fields.");
                }
            }

            foreach (var actionKey in collection.ActionKeys)
            {
                if (!actionKeys.Contains(actionKey))
                {
                    errors.Add($"{path}.action_keys references unknown action '{actionKey}'.");
                }
            }
        }
    }

    private static void ValidateRelationship(ErpEntity entity, ErpField field, List<string> errors)
    {
        if (field.Relationship is null)
        {
            return;
        }

        var path = $"entity[{entity.Id}].field[{field.Id}].relationship";
        if (!field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(field.References))
        {
            errors.Add($"{path} can only be declared on a relation field.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(field.Relationship.Key))
        {
            RequireIdentifier(field.Relationship.Key, $"{path}.key", errors);
        }

        if (!field.Relationship.Cardinality.Equals("many_to_one", StringComparison.Ordinal))
        {
            errors.Add($"{path}.cardinality must be exactly 'many_to_one'.");
        }

        if (!string.IsNullOrWhiteSpace(field.Relationship.Inverse))
        {
            RequireIdentifier(field.Relationship.Inverse, $"{path}.inverse", errors);
        }

        if (!field.Relationship.ArchiveBehavior.Equals("restrict", StringComparison.Ordinal))
        {
            errors.Add($"{path}.archive_behavior must be exactly 'restrict'.");
        }
    }

    private static void ValidateCollectionFields(
        string path,
        string member,
        IReadOnlyList<string> fields,
        IReadOnlyDictionary<string, ErpField> childFields,
        List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!seen.Add(field))
            {
                errors.Add($"{path}.{member} contains duplicate field '{field}'.");
            }
            else if (!childFields.ContainsKey(field))
            {
                errors.Add($"{path}.{member} references unknown child field '{field}'.");
            }
        }
    }

    public static void ValidateRelationCreateGuards(
        ErpEntity entity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guard in entity.RelationCreateGuards)
        {
            var path = $"entity[{entity.Id}].relation_create_guard[{guard.RelationField}->{guard.ReferencedField}]";
            if (!seen.Add($"{guard.RelationField}\u001f{guard.ReferencedField}"))
            {
                errors.Add($"{path} is duplicated.");
                continue;
            }

            var relation = entity.Fields.FirstOrDefault(field =>
                field.Id.Equals(guard.RelationField, StringComparison.Ordinal));
            if (relation is null ||
                !relation.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                !relation.Required || relation.Computed is not null ||
                string.IsNullOrWhiteSpace(relation.References) ||
                !fieldsByEntity.TryGetValue(relation.References, out var referencedFields))
            {
                errors.Add($"{path}.relation_field must resolve to one required, noncomputed relation field.");
                continue;
            }

            var referenced = referencedFields.Values.FirstOrDefault(field =>
                field.Id.Equals(guard.ReferencedField, StringComparison.Ordinal));
            if (referenced is null ||
                referenced.Computed is not null ||
                !referenced.Type.Equals("enum", StringComparison.OrdinalIgnoreCase) &&
                !referenced.Type.Equals("string", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}.referenced_field must resolve to one materialized enum or string field.");
                continue;
            }

            if (!guard.Op.Equals("eq", StringComparison.Ordinal))
            {
                errors.Add($"{path}.op must be exactly 'eq'.");
            }

            var literal = Convert.ToString(guard.Value?.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (guard.Value is null || !guard.Value.Type.Equals("string", StringComparison.Ordinal) ||
                !IsSafeRelationGuardLiteral(literal))
            {
                errors.Add($"{path}.value must be one typed safe string literal.");
                continue;
            }

            if (referenced.Type.Equals("enum", StringComparison.OrdinalIgnoreCase) &&
                !referenced.Options.Contains(literal, StringComparer.Ordinal))
            {
                errors.Add($"{path}.value '{literal}' must exactly equal one referenced enum option.");
            }
        }
    }

    private static bool IsSafeRelationGuardLiteral(string value) =>
        value.Length is > 0 and <= 120 && char.IsLetterOrDigit(value[0]) &&
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or ':' or '-');

    static void ValidateDisplay(ErpEntity entity, HashSet<string> fieldNames, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(entity.Display.Primary) && entity.Display.Primary != "id" && !fieldNames.Contains(entity.Display.Primary))
        {
            errors.Add($"Entity '{entity.Id}' display.primary references unknown field '{entity.Display.Primary}'.");
        }

        if (!string.IsNullOrWhiteSpace(entity.Display.Secondary) && entity.Display.Secondary != "id" && !fieldNames.Contains(entity.Display.Secondary))
        {
            errors.Add($"Entity '{entity.Id}' display.secondary references unknown field '{entity.Display.Secondary}'.");
        }

        RejectSqlLikeContent(entity.Display.Primary, $"entity[{entity.Id}].display.primary", errors);
        RejectSqlLikeContent(entity.Display.Secondary, $"entity[{entity.Id}].display.secondary", errors);
    }

    static void ValidateComputedFields(
        ErpEntity entity,
        IReadOnlyDictionary<string, ErpField> fields,
        List<string> errors)
    {
        foreach (var field in entity.Fields.Where(field => field.Computed is not null))
        {
            var path = $"entity[{entity.Id}].field[{field.Id}].computed";
            var computed = field.Computed!;
            var op = computed.Op.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(op))
            {
                errors.Add($"{path}.op is required.");
                continue;
            }

            if (!ComputedOps.Contains(op))
            {
                errors.Add($"Computed field '{entity.Id}.{field.Id}' uses unsupported op '{computed.Op}'.");
                continue;
            }

            RejectSqlLikeContent(computed.Op, $"{path}.op", errors);

            var leftField = ValidateComputedOperand(entity, field, computed.Left, $"{path}.left", fields, errors);
            var rightField = ValidateComputedOperand(entity, field, computed.Right, $"{path}.right", fields, errors);

            if (op is "add" or "subtract" or "multiply" or "divide")
            {
                if (!ArithmeticFieldTypes.Contains(field.Type))
                {
                    errors.Add($"Computed field '{entity.Id}.{field.Id}' op '{op}' must use a numeric field type.");
                }

                if (leftField is not null && !ArithmeticFieldTypes.Contains(leftField.Type))
                {
                    errors.Add($"Computed field '{entity.Id}.{field.Id}' op '{op}' requires numeric left field '{leftField.Name}'.");
                }

                if (rightField is not null && !ArithmeticFieldTypes.Contains(rightField.Type))
                {
                    errors.Add($"Computed field '{entity.Id}.{field.Id}' op '{op}' requires numeric right field '{rightField.Name}'.");
                }

                if (computed.Left?.Value is not null && !IsNumericValue(computed.Left.Value))
                {
                    errors.Add($"Computed field '{entity.Id}.{field.Id}' op '{op}' requires a numeric left value.");
                }

                if (computed.Right?.Value is not null && !IsNumericValue(computed.Right.Value))
                {
                    errors.Add($"Computed field '{entity.Id}.{field.Id}' op '{op}' requires a numeric right value.");
                }
            }

            if (op == "concat" && !IsTextType(field.Type))
            {
                errors.Add($"Computed field '{entity.Id}.{field.Id}' op 'concat' must use a text field type.");
            }
        }
    }

    static ErpField? ValidateComputedOperand(
        ErpEntity entity,
        ErpField computedField,
        ErpComputedOperand? operand,
        string path,
        IReadOnlyDictionary<string, ErpField> fields,
        List<string> errors)
    {
        if (operand is null)
        {
            errors.Add($"{path} is required.");
            return null;
        }

        var fieldSpecified = !string.IsNullOrWhiteSpace(operand.Field);
        var valueSpecified = operand.Value is not null;
        if (fieldSpecified == valueSpecified)
        {
            errors.Add($"{path} must declare exactly one of field or value.");
            return null;
        }

        RejectSqlLikeContent(operand.Field, $"{path}.field", errors);
        RejectSqlLikeValue(operand.Value, $"{path}.value", errors);

        if (!fieldSpecified)
        {
            return null;
        }

        RequireIdentifier(operand.Field, $"{path}.field", errors);
        if (!fields.TryGetValue(operand.Field, out var field))
        {
            errors.Add($"Computed field '{entity.Id}.{computedField.Id}' references unknown field '{operand.Field}'.");
            return null;
        }

        if (field.Id.Equals(computedField.Id, StringComparison.OrdinalIgnoreCase) ||
            field.Name.Equals(computedField.Name, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Computed field '{entity.Id}.{computedField.Id}' cannot reference itself.");
            return null;
        }

        if (field.Computed is not null)
        {
            errors.Add($"Computed field '{entity.Id}.{computedField.Id}' cannot reference another computed field '{field.Name}' in this pass.");
        }

        return field;
    }
}
