using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    private static int IndexOfStep(IReadOnlyList<RuntimeWorkflowStep> steps, string stepId)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].Id.Equals(stepId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string[] RoleArray(
        RuntimePermissionSnapshot snapshot) =>
        snapshot.EffectiveRoleIds
        .Where(role => !string.IsNullOrWhiteSpace(role))
        .Select(role => role.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ToActionLabel(string action)
    {
        var words = action.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadMetadataString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IReadOnlyList<string> ReadMetadataStringArray(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray()
            : [];

    private static int? ReadMetadataInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static void EnsureIdentifier(string value)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw new InvalidOperationException($"Unsafe generated identifier '{value}'.");
        }
    }

    private static string RuntimeFieldKey(
        RuntimeEntityDefinition entity,
        RuntimeFieldDefinition field)
    {
        var prefix = $"{entity.Id}.";
        return field.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? field.Id[prefix.Length..]
            : field.Id;
    }

    private static string Quote(string value)
    {
        EnsureIdentifier(value);
        return $"\"{value}\"";
    }

    private sealed record RuntimeWorkflowExecutionResult(RuntimeWorkflowTask? PendingTask);

    private sealed record RuntimeTaskAssignment(
        long? UserId,
        string? Role,
        string AssignmentType,
        string? Path,
        string Source,
        bool FellBack)
    {
        public static RuntimeTaskAssignment UserAssignment(long userId, string source, string? path) =>
            new(userId, null, "relation_user", path, source, false);

        public static RuntimeTaskAssignment RoleAssignment(string role) =>
            new(null, role, "role", null, "role", false);

        public static RuntimeTaskAssignment FallbackRole(string role, string? path) =>
            new(null, role, "relation_user", path, "fallback_role", true);

        public static RuntimeTaskAssignment Unassigned(
            string assignmentType = "none",
            string? path = null,
            string source = "unassigned") =>
            new(null, null, assignmentType, path, source, false);
    }

    private sealed record RuntimeUserLinkResolution(long? UserId, bool IsAmbiguous);
}
