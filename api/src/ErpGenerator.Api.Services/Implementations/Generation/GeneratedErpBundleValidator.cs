using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static partial class GeneratedErpBundleValidator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> FieldTypes = new(StringComparer.Ordinal)
    {
        "text",
        "long_text",
        "integer",
        "decimal",
        "boolean",
        "date",
        "datetime",
        "reference"
    };
    private static readonly HashSet<string> PermissionActions = new(StringComparer.Ordinal)
    {
        "read",
        "create",
        "update",
        "archive",
        "restore",
        "start_workflow",
        "approve_task",
        "reject_task",
        "read_task",
        "admin"
    };
    private static readonly HashSet<string> ReservedFieldIdentifiers =
        new(StringComparer.OrdinalIgnoreCase)
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

    public static void Validate(GeneratedErpBundle bundle)
    {
        var errors = new List<string>();
        RequireText(bundle.Name, "name", errors);
        RequireText(bundle.Summary, "summary", errors);
        if (bundle.Modules.Count == 0 ||
            bundle.Modules.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("modules must contain at least one named module");
        }
        if (bundle.Entities.Count == 0)
        {
            errors.Add("entities must contain at least one entity");
        }

        var entities = ValidateEntities(bundle.Entities, errors);
        var reports = ValidateReports(bundle.Reports, entities, errors);
        ValidateWorkflows(bundle.Workflows, entities, errors);
        ValidateDashboards(bundle.Dashboards, reports, errors);
        ValidatePermissions(bundle.Permissions, entities.Keys, reports.Keys, bundle.Dashboards, errors);
        ValidateAcceptanceTests(bundle.AcceptanceTests, entities, reports.Keys, errors);

        if (BundleStrings(bundle).Any(value => SqlPattern().IsMatch(value)))
        {
            errors.Add("bundle text contains prohibited SQL content");
        }

        if (errors.Count == 0) return;
        throw new GeneratedBundleValidationException(
            "Generated ERP bundle does not satisfy the Runtime contract: " +
            string.Join("; ", errors.Take(12)) +
            (errors.Count > 12 ? $"; and {errors.Count - 12} more validation errors" : string.Empty) +
            ".");
    }

    private static Dictionary<string, GeneratedEntityDefinition> ValidateEntities(
        IReadOnlyList<GeneratedEntityDefinition> definitions,
        List<string> errors)
    {
        var entities = new Dictionary<string, GeneratedEntityDefinition>(StringComparer.Ordinal);
        var entityIdentityOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in definitions)
        {
            RequireIdentifier(entity.Key, $"entity '{entity.Key}' key", errors);
            RequireText(entity.Label, $"entity '{entity.Key}' label", errors);
            if (!entities.TryAdd(entity.Key, entity))
            {
                errors.Add($"entity key '{entity.Key}' is duplicated");
            }
            foreach (var identity in new[] { entity.Key, $"{entity.Key}s" })
            {
                if (string.IsNullOrWhiteSpace(identity)) continue;
                if (entityIdentityOwners.TryGetValue(identity, out var owner) &&
                    !owner.Equals(entity.Key, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"entity key '{entity.Key}' conflicts with the Runtime identity of entity '{owner}'");
                }
                else
                {
                    entityIdentityOwners[identity] = entity.Key;
                }
            }

            if (entity.Fields.Count == 0)
            {
                errors.Add($"entity '{entity.Key}' must contain at least one field");
                continue;
            }

            var fields = new HashSet<string>(StringComparer.Ordinal);
            var fieldIdentityOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in entity.Fields)
            {
                RequireIdentifier(field.Key, $"field '{entity.Key}.{field.Key}' key", errors);
                RequireText(field.Label, $"field '{entity.Key}.{field.Key}' label", errors);
                if (!FieldTypes.Contains(field.Type))
                {
                    errors.Add(
                        $"field '{entity.Key}.{field.Key}' has unsupported type '{field.Type}'");
                }
                if (!fields.Add(field.Key))
                {
                    errors.Add($"field key '{entity.Key}.{field.Key}' is duplicated");
                }

                var runtimeName = RuntimeFieldName(field);
                RequireIdentifier(runtimeName, $"field '{entity.Key}.{field.Key}' Runtime name", errors);
                foreach (var identity in RuntimeFieldIdentities(field, runtimeName))
                {
                    if (ReservedFieldIdentifiers.Contains(identity))
                    {
                        errors.Add(
                            $"field '{entity.Key}.{field.Key}' conflicts with reserved Runtime field '{identity}'");
                    }
                    if (identity.EndsWith("__label", StringComparison.OrdinalIgnoreCase) ||
                        identity.EndsWith("__secondaryLabel", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(
                            $"field '{entity.Key}.{field.Key}' uses a reserved Runtime relation-label suffix");
                    }
                    if (fieldIdentityOwners.TryGetValue(identity, out var owner) &&
                        !owner.Equals(field.Key, StringComparison.Ordinal))
                    {
                        errors.Add(
                            $"field '{entity.Key}.{field.Key}' conflicts with field '{entity.Key}.{owner}' in the Runtime identity namespace");
                    }
                    else
                    {
                        fieldIdentityOwners[identity] = field.Key;
                    }
                }
            }
        }

        foreach (var entity in definitions)
        {
            foreach (var field in entity.Fields)
            {
                if (field.Type == "reference")
                {
                    if (string.IsNullOrWhiteSpace(field.References) ||
                        !entities.ContainsKey(field.References))
                    {
                        errors.Add(
                            $"reference field '{entity.Key}.{field.Key}' must name an exact entity key");
                    }
                }
                else if (field.References is not null)
                {
                    errors.Add(
                        $"non-reference field '{entity.Key}.{field.Key}' must set references to null");
                }
            }
        }

        return entities;
    }

    private static Dictionary<string, GeneratedReportDefinition> ValidateReports(
        IReadOnlyList<GeneratedReportDefinition> definitions,
        IReadOnlyDictionary<string, GeneratedEntityDefinition> entities,
        List<string> errors)
    {
        var reports = new Dictionary<string, GeneratedReportDefinition>(StringComparer.Ordinal);
        foreach (var report in definitions)
        {
            RequireIdentifier(report.Key, $"report '{report.Key}' key", errors);
            RequireText(report.Label, $"report '{report.Key}' label", errors);
            if (!reports.TryAdd(report.Key, report))
            {
                errors.Add($"report key '{report.Key}' is duplicated");
            }
            if (!entities.TryGetValue(report.EntityKey, out var entity))
            {
                errors.Add(
                    $"report '{report.Key}' must name an exact entity key");
                continue;
            }
            if (report.Columns.Count == 0)
            {
                errors.Add($"report '{report.Key}' must contain at least one column");
                continue;
            }
            var fields = entity.Fields.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
            if (report.Columns.Distinct(StringComparer.Ordinal).Count() != report.Columns.Count ||
                report.Columns.Any(column => !fields.Contains(column)))
            {
                errors.Add(
                    $"report '{report.Key}' columns must be unique exact field keys on entity '{entity.Key}'");
            }
        }
        return reports;
    }

    private static void ValidateWorkflows(
        IReadOnlyList<GeneratedWorkflowDefinition> definitions,
        IReadOnlyDictionary<string, GeneratedEntityDefinition> entities,
        List<string> errors)
    {
        var workflowKeys = new HashSet<string>(StringComparer.Ordinal);
        var triggerIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in definitions)
        {
            RequireIdentifier(workflow.Key, $"workflow '{workflow.Key}' key", errors);
            RequireText(workflow.Label, $"workflow '{workflow.Key}' label", errors);
            RequireIdentifier(
                workflow.TriggerAction,
                $"workflow '{workflow.Key}' triggerAction",
                errors);
            if (!workflowKeys.Add(workflow.Key))
            {
                errors.Add($"workflow key '{workflow.Key}' is duplicated");
            }
            if (!entities.TryGetValue(workflow.EntityKey, out var entity))
            {
                errors.Add(
                    $"workflow '{workflow.Key}' must name an exact entity key");
                continue;
            }
            if (!triggerIdentities.Add($"{workflow.EntityKey}\u001f{workflow.TriggerAction}"))
            {
                errors.Add(
                    $"workflow '{workflow.Key}' duplicates the entity and triggerAction of another workflow");
            }
            if (!string.IsNullOrWhiteSpace(workflow.ApprovalRole))
            {
                RequireIdentifier(
                    NormalizeRoleKey(workflow.ApprovalRole),
                    $"workflow '{workflow.Key}' approvalRole",
                    errors);
            }

            var hasStatusField = !string.IsNullOrWhiteSpace(workflow.StatusField);
            var hasApprovedValue = !string.IsNullOrWhiteSpace(workflow.ApprovedValue);
            if (hasStatusField != hasApprovedValue)
            {
                errors.Add(
                    $"workflow '{workflow.Key}' must provide both statusField and approvedValue or neither");
            }
            else if (hasStatusField &&
                     entity.Fields.All(field => field.Key != workflow.StatusField))
            {
                errors.Add(
                    $"workflow '{workflow.Key}' statusField must be an exact field key on entity '{entity.Key}'");
            }
        }
    }

    private static void ValidateDashboards(
        IReadOnlyList<GeneratedDashboardDefinition> definitions,
        IReadOnlyDictionary<string, GeneratedReportDefinition> reports,
        List<string> errors)
    {
        var dashboardKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dashboard in definitions)
        {
            RequireIdentifier(dashboard.Key, $"dashboard '{dashboard.Key}' key", errors);
            RequireText(dashboard.Label, $"dashboard '{dashboard.Key}' label", errors);
            if (!dashboardKeys.Add(dashboard.Key))
            {
                errors.Add($"dashboard key '{dashboard.Key}' is duplicated");
            }
            if (dashboard.Reports.Count == 0 ||
                dashboard.Reports.Distinct(StringComparer.Ordinal).Count() !=
                dashboard.Reports.Count ||
                dashboard.Reports.Any(report => !reports.ContainsKey(report)))
            {
                errors.Add(
                    $"dashboard '{dashboard.Key}' must contain unique exact report keys");
            }
        }
    }

    private static void ValidatePermissions(
        IReadOnlyList<GeneratedPermissionDefinition> definitions,
        IEnumerable<string> entityKeys,
        IEnumerable<string> reportKeys,
        IReadOnlyList<GeneratedDashboardDefinition> dashboards,
        List<string> errors)
    {
        var entities = entityKeys.ToHashSet(StringComparer.Ordinal);
        var reports = reportKeys.ToHashSet(StringComparer.Ordinal);
        var dashboardKeys = dashboards.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        var roleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var permission in definitions)
        {
            var roleKey = NormalizeRoleKey(permission.Role);
            RequireIdentifier(roleKey, $"permission role '{permission.Role}'", errors);
            if (!roleKeys.Add(roleKey))
            {
                errors.Add(
                    $"permission role '{permission.Role}' duplicates normalized role '{roleKey}'");
            }
            if (permission.Grants.Count == 0)
            {
                errors.Add($"permission role '{permission.Role}' must contain at least one grant");
            }
            if (permission.Grants.Distinct(StringComparer.Ordinal).Count() != permission.Grants.Count)
            {
                errors.Add($"permission role '{permission.Role}' contains duplicate grants");
            }

            foreach (var grant in permission.Grants)
            {
                var parts = grant.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !PermissionActions.Contains(parts[0]) ||
                    string.IsNullOrWhiteSpace(parts[1]))
                {
                    errors.Add(
                        $"permission grant '{grant}' must use one supported action:resource pair");
                    continue;
                }

                var action = parts[0];
                var resource = parts[1];
                var resourceExists = entities.Contains(resource) ||
                    action == "read" && (reports.Contains(resource) || dashboardKeys.Contains(resource)) ||
                    action == "admin" && resource == "*";
                if (!resourceExists)
                {
                    errors.Add(
                        $"permission grant '{grant}' references an unsupported or unknown resource");
                }
            }
        }
    }

    private static void ValidateAcceptanceTests(
        IReadOnlyList<GeneratedAcceptanceTestDefinition> definitions,
        IReadOnlyDictionary<string, GeneratedEntityDefinition> entities,
        IEnumerable<string> reportKeys,
        List<string> errors)
    {
        var reports = reportKeys.ToHashSet(StringComparer.Ordinal);
        var testKeys = new HashSet<string>(StringComparer.Ordinal);
        if (definitions.Count == 0)
        {
            errors.Add("acceptanceTests must contain at least one executable test");
        }
        foreach (var test in definitions)
        {
            RequireIdentifier(test.Key, $"acceptance test '{test.Key}' key", errors);
            RequireText(test.Label, $"acceptance test '{test.Key}' label", errors);
            if (!testKeys.Add(test.Key))
            {
                errors.Add($"acceptance test key '{test.Key}' is duplicated");
            }
            if (test.Kind == "report_access")
            {
                if (string.IsNullOrWhiteSpace(test.ReportKey) ||
                    !reports.Contains(test.ReportKey) ||
                    test.EntityKey is not null ||
                    test.Values.Count != 0)
                {
                    errors.Add(
                        $"report_access test '{test.Key}' must name one exact report and no entity or values");
                }
                continue;
            }

            if (test.Kind != "entity_create" ||
                string.IsNullOrWhiteSpace(test.EntityKey) ||
                test.ReportKey is not null ||
                !entities.TryGetValue(test.EntityKey, out var entity))
            {
                errors.Add(
                    $"acceptance test '{test.Key}' must be a supported typed test with exact references");
                continue;
            }
            if (entity.Fields.Any(value => value.Required && value.Type == "reference"))
            {
                errors.Add(
                    $"entity_create test '{test.Key}' cannot target entity '{entity.Key}' because it has required reference fields");
            }

            var fields = entity.Fields.ToDictionary(value => value.Key, StringComparer.Ordinal);
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in test.Values)
            {
                if (!values.Add(value.FieldKey) ||
                    !fields.TryGetValue(value.FieldKey, out var field) ||
                    field.Type == "reference")
                {
                    errors.Add(
                        $"entity_create test '{test.Key}' values must use unique exact non-reference field keys");
                    continue;
                }
                if (!IsCompatibleAcceptanceValue(field.Type, value.Value))
                {
                    errors.Add(
                        $"entity_create test '{test.Key}' value for '{value.FieldKey}' does not match type '{field.Type}'");
                }
            }
            if (entity.Fields.Any(value =>
                    value.Required &&
                    value.Type != "reference" &&
                    !values.Contains(value.Key)))
            {
                errors.Add(
                    $"entity_create test '{test.Key}' must provide every required non-reference field");
            }
        }
    }

    private static bool IsCompatibleAcceptanceValue(string fieldType, string value) => fieldType switch
    {
        "integer" => int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out _),
        "decimal" => decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out _),
        "boolean" => bool.TryParse(value, out _),
        "date" => DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _),
        "datetime" => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _),
        _ => true
    };

    private static IEnumerable<string> RuntimeFieldIdentities(
        GeneratedFieldDefinition field,
        string runtimeName)
    {
        yield return runtimeName;
        if (field.Type == "reference" && runtimeName.EndsWith("_id", StringComparison.Ordinal))
        {
            yield return runtimeName[..^3];
        }
    }

    private static string RuntimeFieldName(GeneratedFieldDefinition field) =>
        field.Type == "reference" ? $"{field.Key}_id" : field.Key;

    private static string NormalizeRoleKey(string? value) =>
        value?.Trim().ToLowerInvariant().Replace(' ', '_') ?? string.Empty;

    private static void RequireIdentifier(string value, string path, List<string> errors)
    {
        if (!IdentifierPattern().IsMatch(value ?? string.Empty))
        {
            errors.Add(
                $"{path} must be lower_snake_case, start with a letter, and contain 2 to 63 characters");
        }
    }

    private static void RequireText(string value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} is required");
        }
    }

    private static IEnumerable<string> BundleStrings(GeneratedErpBundle bundle)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(bundle, JsonOptions));
        return EnumerateStrings(document.RootElement).ToArray();
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString() ?? string.Empty;
            yield break;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                foreach (var value in EnumerateStrings(child))
                {
                    yield return value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var value in EnumerateStrings(property.Value))
                {
                    yield return value;
                }
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        @"\b(select\s+.+\s+from|insert\s+into|update\s+\w+\s+set|delete\s+from|create\s+(table|database|schema)|alter\s+table|drop\s+(table|database|schema))\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SqlPattern();
}
