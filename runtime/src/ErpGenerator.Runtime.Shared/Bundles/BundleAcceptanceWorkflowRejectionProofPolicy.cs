using System.Globalization;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Defense-in-depth validation for the executable proof attached to a compiled
/// workflow rejection. The design layer creates this proof; deployment
/// boundaries must independently reject a v3 document that drops any compiled
/// rejection effect.
/// </summary>
public static class BundleAcceptanceWorkflowRejectionProofPolicy
{
    public static string? FindUnsupportedReason(
        BundleAcceptanceTestDocument test,
        BundleAcceptanceStep rejection,
        WorkflowDefinition workflow,
        IReadOnlyList<EntityDefinition> entities)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(rejection);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(entities);

        var rejectionIndex = test.Steps.IndexOf(rejection);
        if (rejectionIndex <= 0)
        {
            return $"Acceptance test '{test.Test.Key}' v3 reject_task step '{rejection.Id}' must have an immediately preceding proof checkpoint.";
        }

        var preRejectionCheckpoint = test.Steps[rejectionIndex - 1].Id;
        if (!test.Assertions.Any(assertion =>
                MatchesRejectionRecord(assertion, rejection) &&
                assertion.Type.Equals("workflow_status", StringComparison.OrdinalIgnoreCase) &&
                assertion.Workflow.Equals(workflow.Id, StringComparison.Ordinal) &&
                assertion.WorkflowRef.Equals(rejection.WorkflowRef, StringComparison.Ordinal) &&
                assertion.Status.Equals("rejected", StringComparison.Ordinal) &&
                assertion.AfterStep.Equals(rejection.Id, StringComparison.Ordinal)))
        {
            return $"Acceptance test '{test.Test.Key}' v3 reject_task step '{rejection.Id}' requires an exact workflow_status rejected assertion for compiled workflow '{workflow.Id}' at the rejection checkpoint.";
        }

        var entity = entities.SingleOrDefault(candidate =>
            candidate.Id.Equals(workflow.Entity, StringComparison.Ordinal));
        var branchSteps = FlattenWorkflowSteps(workflow.OnRejected).ToArray();
        var finalUpdates = branchSteps
            .Where(step => step.Type.Equals("update_record", StringComparison.OrdinalIgnoreCase))
            .SelectMany(step => step.Set)
            .GroupBy(update => update.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        foreach (var update in finalUpdates)
        {
            var field = entity?.Fields.SingleOrDefault(candidate =>
                candidate.Id.Equals(update.Key, StringComparison.OrdinalIgnoreCase));
            if (field is null || !TryConvertCompiledUpdate(field, update.Value, out var finalValue))
            {
                return $"Acceptance test '{test.Test.Key}' v3 reject_task step '{rejection.Id}' cannot prove unknown or invalid compiled rejection field '{workflow.Entity}.{update.Key}'.";
            }

            var hasMeaningfulPreState = test.Assertions.Any(assertion =>
                MatchesRejectionRecord(assertion, rejection) &&
                assertion.Type.Equals("record_field_equals", StringComparison.OrdinalIgnoreCase) &&
                assertion.Field.Equals(update.Key, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(assertion.ValueRef) &&
                assertion.AfterStep.Equals(preRejectionCheckpoint, StringComparison.Ordinal) &&
                TryFieldValuesEqual(field, finalValue, assertion.Value, out var equal) &&
                !equal);
            if (!hasMeaningfulPreState)
            {
                return $"Acceptance test '{test.Test.Key}' v3 reject_task step '{rejection.Id}' requires an exact pre-rejection record_field_equals assertion with a runtime-distinct value for compiled branch field '{workflow.Entity}.{update.Key}' at the immediately preceding checkpoint '{preRejectionCheckpoint}'.";
            }

            var hasExactPostState = test.Assertions.Any(assertion =>
                MatchesRejectionRecord(assertion, rejection) &&
                assertion.Type.Equals("record_field_equals", StringComparison.OrdinalIgnoreCase) &&
                assertion.Field.Equals(update.Key, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(assertion.ValueRef) &&
                assertion.AfterStep.Equals(rejection.Id, StringComparison.Ordinal) &&
                TryFieldValuesEqual(field, finalValue, assertion.Value, out var equal) &&
                equal);
            if (!hasExactPostState)
            {
                return $"Acceptance test '{test.Test.Key}' v3 reject_task step '{rejection.Id}' requires an exact post-rejection record_field_equals assertion for compiled branch field '{workflow.Entity}.{update.Key}' at the rejection checkpoint.";
            }
        }

        foreach (var auditStep in branchSteps.Where(step =>
                     step.Type.Equals("audit_event", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(step.Event)))
        {
            if (!test.Assertions.Any(assertion =>
                    MatchesRejectionRecord(assertion, rejection) &&
                    assertion.Type.Equals("audit_event_exists", StringComparison.OrdinalIgnoreCase) &&
                    assertion.Event.Equals(auditStep.Event, StringComparison.Ordinal) &&
                    assertion.AuditStep.Equals(auditStep.Id, StringComparison.Ordinal) &&
                    assertion.AfterStep.Equals(rejection.Id, StringComparison.Ordinal)))
            {
                return $"Acceptance test '{test.Test.Key}' v3 reject_task step '{rejection.Id}' requires an exact fresh audit_event_exists assertion for compiled rejection audit step '{auditStep.Id}' event '{auditStep.Event}' at the rejection checkpoint.";
            }
        }

        foreach (var notificationStepId in branchSteps
                     .Where(step => step.Type.Equals(
                         "notification_placeholder",
                         StringComparison.OrdinalIgnoreCase))
                     .Select(step => step.Id)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!test.Assertions.Any(assertion =>
                    MatchesRejectionRecord(assertion, rejection) &&
                    assertion.Type.Equals("notification_exists", StringComparison.OrdinalIgnoreCase) &&
                    assertion.WorkflowRef.Equals(rejection.WorkflowRef, StringComparison.Ordinal) &&
                    assertion.TaskStep.Equals(notificationStepId, StringComparison.Ordinal) &&
                    assertion.AfterStep.Equals(rejection.Id, StringComparison.Ordinal)))
            {
                return $"Acceptance test '{test.Test.Key}' v3 reject_task step '{rejection.Id}' requires an exact fresh notification_exists assertion for compiled rejection notification '{notificationStepId}' at the rejection checkpoint.";
            }
        }

        return null;
    }

    private static bool MatchesRejectionRecord(
        BundleAcceptanceAssertion assertion,
        BundleAcceptanceStep rejection) =>
        assertion.Entity.Equals(rejection.Entity, StringComparison.Ordinal) &&
        string.Equals(assertion.Record?.Ref, rejection.Record?.Ref, StringComparison.Ordinal);

    private static IEnumerable<WorkflowStepDefinition> FlattenWorkflowSteps(
        IEnumerable<WorkflowStepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var nested in FlattenWorkflowSteps(step.Then))
            {
                yield return nested;
            }
        }
    }

    private static bool TryConvertCompiledUpdate(
        FieldDefinition field,
        string value,
        out object? converted)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            converted = null;
            return true;
        }

        var trimmed = value.Trim();
        switch (field.Type.Trim().ToLowerInvariant())
        {
            case "string":
            case "text":
                converted = trimmed;
                return true;
            case "enum":
                converted = field.Options.FirstOrDefault(option =>
                    option.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
                return converted is not null;
            case "integer":
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    converted = integer;
                    return true;
                }

                break;
            case "number":
            case "decimal":
            case "money":
                if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    converted = number;
                    return true;
                }

                break;
            case "boolean":
                if (bool.TryParse(trimmed, out var boolean))
                {
                    converted = boolean;
                    return true;
                }

                break;
            case "date":
                if (DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    converted = date;
                    return true;
                }

                break;
            case "datetime":
                if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
                {
                    converted = dateTime;
                    return true;
                }

                break;
            case "relation":
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relationId) &&
                    relationId > 0)
                {
                    converted = relationId;
                    return true;
                }

                break;
        }

        converted = null;
        return false;
    }

    private static bool TryFieldValuesEqual(
        FieldDefinition field,
        object? finalValue,
        object? assertionValue,
        out bool equal)
    {
        if (!TryConvertAssertionValue(field, assertionValue, out var convertedAssertionValue))
        {
            equal = false;
            return false;
        }

        if (finalValue is null || convertedAssertionValue is null)
        {
            equal = finalValue is null && convertedAssertionValue is null;
            return true;
        }

        equal = finalValue is string finalText && convertedAssertionValue is string assertionText
            ? finalText.Equals(assertionText, StringComparison.OrdinalIgnoreCase)
            : finalValue.Equals(convertedAssertionValue);
        return true;
    }

    private static bool TryConvertAssertionValue(
        FieldDefinition field,
        object? value,
        out object? converted)
    {
        value = NormalizeJsonValue(value);
        if (value is null)
        {
            converted = null;
            return !field.Required;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        switch (field.Type.Trim().ToLowerInvariant())
        {
            case "string":
            case "text":
                if (value is string)
                {
                    converted = text;
                    return true;
                }

                break;
            case "enum":
                converted = field.Options.FirstOrDefault(option =>
                    option.Equals(text, StringComparison.OrdinalIgnoreCase));
                return converted is not null;
            case "integer":
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    converted = integer;
                    return true;
                }

                break;
            case "number":
            case "decimal":
            case "money":
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    converted = number;
                    return true;
                }

                break;
            case "boolean":
                if (bool.TryParse(text, out var boolean))
                {
                    converted = boolean;
                    return true;
                }

                break;
            case "date":
                if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    converted = date;
                    return true;
                }

                break;
            case "datetime":
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
                {
                    converted = dateTime;
                    return true;
                }

                break;
            case "relation":
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relationId) &&
                    relationId > 0)
                {
                    converted = relationId;
                    return true;
                }

                break;
        }

        converted = null;
        return false;
    }

    private static object? NormalizeJsonValue(object? value) =>
        value is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            }
            : value;
}
