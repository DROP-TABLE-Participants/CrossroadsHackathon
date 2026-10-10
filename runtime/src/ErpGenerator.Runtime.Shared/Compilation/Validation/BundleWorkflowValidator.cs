using System.Globalization;
using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleWorkflowValidator
{
    public static void ValidateWorkflow(
        ErpWorkflow workflow,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        HashSet<string> roleIds,
        IReadOnlyDictionary<string, ErpRole> roles,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(workflow.Trigger.Action))
        {
            RequireIdentifier(workflow.Trigger.Action, $"workflow[{workflow.Id}].trigger.action", errors);
        }

        RejectSqlLikeContent(workflow.Trigger.Action, $"workflow[{workflow.Id}].trigger.action", errors);

        ValidateStartGuards(workflow, fieldsByEntity, errors);

        foreach (var state in workflow.States)
        {
            RequireIdentifier(state, $"workflow[{workflow.Id}].state", errors);
            RejectSqlLikeContent(state, $"workflow[{workflow.Id}].state", errors);
        }

        if (workflow.OnRejected.Count > 0 && !HasReachableApprovalStep(workflow.Steps))
        {
            errors.Add(
                $"Workflow '{workflow.Id}' declares a rejection branch but has no reachable approval step in its main steps.");
        }

        var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateWorkflowSteps(workflow, workflow.Steps, "steps", fieldsByEntity, roleIds, roles, errors, stepIds);
        ValidateWorkflowSteps(
            workflow,
            workflow.OnRejected,
            "onRejected",
            fieldsByEntity,
            roleIds,
            roles,
            errors,
            stepIds,
            isRejectionBranch: true);
    }

    private static void ValidateStartGuards(
        ErpWorkflow workflow,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var supportedFilterOperators = new HashSet<string>(
            ["eq", "neq", "gt", "gte", "lt", "lte"],
            StringComparer.Ordinal);
        var guardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        fieldsByEntity.TryGetValue(workflow.Entity, out var sourceFields);
        foreach (var guard in workflow.StartGuards)
        {
            var path = $"workflow[{workflow.Id}].start_guards[{guard.Id}]";
            RequireIdentifier(guard.Id, $"{path}.id", errors);
            RejectSqlLikeContent(guard.Id, $"{path}.id", errors);
            RejectSqlLikeContent(guard.Error, $"{path}.error", errors);
            if (!guardIds.Add(guard.Id))
            {
                errors.Add($"Workflow '{workflow.Id}' contains duplicate start guard id '{guard.Id}'.");
            }

            if (!fieldsByEntity.TryGetValue(guard.Entity, out var targetFields) ||
                !fieldsByEntity.ContainsKey(guard.Entity) ||
                !fieldsByEntity.Keys.Contains(guard.Entity, StringComparer.Ordinal))
            {
                errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' references unknown or non-canonical entity '{guard.Entity}'.");
                continue;
            }

            if (guard.Where.Count == 0)
            {
                errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' must declare at least one typed filter.");
            }

            if (guard.ResultCountOp is not ("eq" or "neq" or "gt" or "gte" or "lt" or "lte"))
            {
                errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' uses unsupported result_count operator '{guard.ResultCountOp}'.");
            }

            if (guard.ResultCount is < 0 or > ErpWorkflowStartGuardLimits.MaxResultCount)
            {
                errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' result_count must be between zero and {ErpWorkflowStartGuardLimits.MaxResultCount}.");
            }
            else if (guard.ResultCountOp == "gt" &&
                     guard.ResultCount == ErpWorkflowStartGuardLimits.MaxResultCount)
            {
                errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' cannot require more than the bounded maximum of {ErpWorkflowStartGuardLimits.MaxResultCount} matching records.");
            }

            foreach (var filter in guard.Where.Where(filter =>
                         !supportedFilterOperators.Contains(filter.Op)))
            {
                errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' uses unsupported filter operator '{filter.Op}'.");
            }

            foreach (var filter in guard.Where)
            {
                var hasLiteral = filter.Value is not null;
                var hasValueFrom = filter.ValueFrom is not null;
                if (filter.Values.Count != 0)
                {
                    errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' filter '{filter.Field}' cannot declare values; use one scalar value or value_from operand.");
                }

                if (hasLiteral == hasValueFrom)
                {
                    errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' filter '{filter.Field}' must declare exactly one scalar value or value_from operand.");
                }
                else if (hasValueFrom && !IsExactCurrentRecordValueFrom(filter.ValueFrom!))
                {
                    errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' filter '{filter.Field}' value_from must reference one exact current-record field.");
                }
            }

            var synthetic = new ErpAction
            {
                Key = "workflow_start_guard_validation",
                Label = "Workflow start guard",
                Entity = workflow.Entity,
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions = workflow.Entity.Equals(guard.Entity, StringComparison.Ordinal)
                        ? [new ErpGrant { Entity = workflow.Entity, Action = "read" }]
                        :
                        [
                            new ErpGrant { Entity = workflow.Entity, Action = "read" },
                            new ErpGrant { Entity = guard.Entity, Action = "read" }
                        ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "guard_query",
                        Type = "query_records",
                        Entity = guard.Entity,
                        Where = guard.Where
                    },
                    new ErpActionStep
                    {
                        Id = "guard_result",
                        Type = "guard",
                        Condition = new ErpActionCondition
                        {
                            Op = guard.ResultCountOp,
                            Left = new ErpActionOperand { ResultCount = "guard_query" },
                            Right = new ErpActionOperand { Value = guard.ResultCount }
                        },
                        Error = guard.Error
                    }
                ]
            };
            var syntheticErrors = new List<string>();
            BundleActionValidator.ValidateActions(
                new ErpBundle { Actions = [synthetic] },
                fieldsByEntity,
                syntheticErrors);
            foreach (var error in syntheticErrors)
            {
                errors.Add(error.Replace(
                    $"Action '{synthetic.Key}'",
                    $"Workflow '{workflow.Id}' start guard '{guard.Id}'",
                    StringComparison.Ordinal));
            }

            var hasExactCorrelation = false;
            foreach (var filter in guard.Where)
            {
                var targetField = targetFields.Values.FirstOrDefault(field =>
                    field.Id.Equals(filter.Field, StringComparison.Ordinal));
                if (!filter.Field.Equals("id", StringComparison.Ordinal) && targetField is null)
                {
                    errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' filter field '{filter.Field}' must use an exact canonical field id.");
                    continue;
                }

                if (filter.Value is not null &&
                    !IsExactStartGuardLiteralCompatible(targetField, filter.Field, filter.Value))
                {
                    errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' literal for '{guard.Entity}.{filter.Field}' must exactly match the runtime field type and canonical options.");
                }

                if (filter.ValueFrom is not { Record: { Length: > 0 } sourceFieldId } ||
                    !string.IsNullOrWhiteSpace(filter.ValueFrom.Item) ||
                    !string.IsNullOrWhiteSpace(filter.ValueFrom.Step) ||
                    !string.IsNullOrWhiteSpace(filter.ValueFrom.Field) ||
                    !string.IsNullOrWhiteSpace(filter.ValueFrom.Now))
                {
                    continue;
                }

                var sourceField = sourceFields?.Values.FirstOrDefault(field =>
                    field.Id.Equals(sourceFieldId, StringComparison.Ordinal));
                if (!sourceFieldId.Equals("id", StringComparison.Ordinal) && sourceField is null)
                {
                    errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' source field '{sourceFieldId}' must use an exact canonical field id.");
                    continue;
                }

                if (!filter.Op.Equals("eq", StringComparison.Ordinal))
                {
                    continue;
                }

                var targetReference = filter.Field.Equals("id", StringComparison.Ordinal)
                    ? guard.Entity
                    : targetField?.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) == true
                        ? targetField.References
                        : null;
                var sourceReference = sourceFieldId.Equals("id", StringComparison.Ordinal)
                    ? workflow.Entity
                    : sourceField?.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) == true
                        ? sourceField.References
                        : null;
                if (!string.IsNullOrWhiteSpace(targetReference) &&
                    targetReference.Equals(sourceReference, StringComparison.Ordinal))
                {
                    hasExactCorrelation = true;
                }
            }

            if (!hasExactCorrelation)
            {
                errors.Add($"Workflow '{workflow.Id}' start guard '{guard.Id}' must correlate entity '{guard.Entity}' to the current '{workflow.Entity}' record through one exact canonical relation equality.");
            }
        }
    }

    private static bool IsExactCurrentRecordValueFrom(ErpActionValueFrom valueFrom) =>
        !string.IsNullOrWhiteSpace(valueFrom.Record) &&
        string.IsNullOrWhiteSpace(valueFrom.Item) &&
        string.IsNullOrWhiteSpace(valueFrom.Step) &&
        string.IsNullOrWhiteSpace(valueFrom.Field) &&
        string.IsNullOrWhiteSpace(valueFrom.Now);

    private static bool IsExactStartGuardLiteralCompatible(
        ErpField? field,
        string fieldId,
        object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (fieldId.Equals("id", StringComparison.Ordinal))
        {
            return long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _);
        }

        return field?.Type.ToLowerInvariant() switch
        {
            "boolean" => value is bool || bool.TryParse(text, out _),
            "integer" or "relation" => long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _),
            "number" or "decimal" or "money" => decimal.TryParse(
                text,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out _),
            "date" => value is DateOnly || DateOnly.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            "datetime" => value is DateTimeOffset || DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out _),
            "enum" => field.Options.Contains(text, StringComparer.Ordinal),
            "string" or "text" => true,
            _ => false
        };
    }

    static void ValidateWorkflowSteps(
        ErpWorkflow workflow,
        IReadOnlyList<ErpWorkflowStep> steps,
        string collectionName,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        HashSet<string> roleIds,
        IReadOnlyDictionary<string, ErpRole> roles,
        List<string> errors,
        HashSet<string> stepIds,
        bool isRejectionBranch = false)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var path = $"workflow[{workflow.Id}].{collectionName}[{index}]";
            RequireIdentifier(step.Id, $"{path}.id", errors);
            RejectSqlLikeContent(step.Id, $"{path}.id", errors);

            if (!stepIds.Add(step.Id))
            {
                errors.Add($"Workflow '{workflow.Id}' contains duplicate step id '{step.Id}'.");
            }

            if (!WorkflowStepTypes.Contains(step.Type))
            {
                errors.Add($"Workflow '{workflow.Id}' step '{step.Id}' uses unsupported type '{step.Type}'.");
            }

            if (isRejectionBranch && !TerminalRejectionStepTypes.Contains(step.Type))
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' rejection branch step '{step.Id}' uses non-terminal type '{step.Type}'. " +
                    "Rejection branches may contain only update_record, audit_event, or notification_placeholder steps.");
            }

            if (isRejectionBranch && TerminalRejectionStepTypes.Contains(step.Type))
            {
                ValidateTerminalRejectionStepPayload(workflow, step, errors);
            }

            if (isRejectionBranch && step.Then.Count > 0)
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' rejection branch step '{step.Id}' must be terminal and cannot declare nested then steps.");
            }

            RejectSqlLikeContent(step.Type, $"{path}.type", errors);
            RejectSqlLikeContent(step.Label, $"{path}.label", errors);
            RejectSqlLikeContent(step.ApproveLabel, $"{path}.approveLabel", errors);
            RejectSqlLikeContent(step.RejectLabel, $"{path}.rejectLabel", errors);
            RejectSqlLikeContent(step.Message, $"{path}.message", errors);

            if (!step.Type.Equals("approval", StringComparison.OrdinalIgnoreCase) &&
                step.ReviewFields.Count > 0)
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' non-approval step '{step.Id}' cannot declare reviewFields.");
            }

            if (step.Type.Equals("approval", StringComparison.OrdinalIgnoreCase))
            {
                ValidateWorkflowAssignment(workflow, step, path, fieldsByEntity, roleIds, roles, errors);
            }

            if (step.Type.Equals("condition", StringComparison.OrdinalIgnoreCase))
            {
                var condition = step.Condition ?? step.If;
                if (condition is null)
                {
                    errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' must declare condition or if.");
                }
                else
                {
                    ValidateWorkflowCondition(workflow, step, condition, $"{path}.condition", workflow.Entity, fieldsByEntity, errors);
                }

                if (step.Then.Count == 0)
                {
                    errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' must declare at least one then step.");
                }
            }

            var entityFields = fieldsByEntity.TryGetValue(workflow.Entity, out var workflowEntityFields)
                ? workflowEntityFields
                : new Dictionary<string, ErpField>(StringComparer.OrdinalIgnoreCase);

            if (step.Type.Equals("update_record", StringComparison.OrdinalIgnoreCase) && step.Set.Count == 0)
            {
                errors.Add($"Workflow '{workflow.Id}' update_record step '{step.Id}' must declare set values.");
            }

            foreach (var (fieldName, value) in step.Set)
            {
                RequireIdentifier(fieldName, $"{path}.set.field", errors);
                RejectSqlLikeContent(fieldName, $"{path}.set.field", errors);
                RejectSqlLikeContent(value, $"{path}.set[{fieldName}]", errors);

                if (!entityFields.TryGetValue(fieldName, out var field))
                {
                    errors.Add($"Workflow '{workflow.Id}' step '{step.Id}' sets unknown field '{fieldName}'.");
                    continue;
                }

                if (field.Computed is not null)
                {
                    errors.Add(
                        $"Workflow '{workflow.Id}' step '{step.Id}' cannot set computed field '{workflow.Entity}.{field.Id}'.");
                }

                if (IsNamedActionLifecycle(field.Lifecycle?.Transitions))
                {
                    errors.Add(
                        $"Workflow '{workflow.Id}' step '{step.Id}' cannot set named-action lifecycle field '{workflow.Entity}.{field.Id}'.");
                }

                if (value is null)
                {
                    errors.Add(field.Required
                        ? $"Workflow '{workflow.Id}' step '{step.Id}' cannot set required field '{workflow.Entity}.{field.Id}' to null."
                        : $"Workflow '{workflow.Id}' step '{step.Id}' cannot declare a null payload for optional field '{workflow.Entity}.{field.Id}'; use a blank value to request runtime null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    if (field.Required)
                    {
                        errors.Add(
                            $"Workflow '{workflow.Id}' step '{step.Id}' cannot set required field '{workflow.Entity}.{field.Id}' to a blank value because workflow runtime converts blank values to null.");
                    }
                    else if (isRejectionBranch)
                    {
                        errors.Add(
                            $"Workflow '{workflow.Id}' rejection branch step '{step.Id}' cannot clear optional field '{workflow.Entity}.{field.Id}' with a blank value because exact rejection evidence cannot represent runtime null.");
                    }

                    continue;
                }

                if (field.Type.Equals("enum", StringComparison.OrdinalIgnoreCase) &&
                    !field.Options.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"Workflow '{workflow.Id}' step '{step.Id}' sets enum field '{workflow.Entity}.{field.Id}' to unsupported value '{value}'.");
                }
                else if (field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
                         (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relationId) ||
                          relationId <= 0))
                {
                    errors.Add(
                        $"Workflow '{workflow.Id}' step '{step.Id}' must set relation field '{workflow.Entity}.{field.Id}' to a positive integer identifier, but value '{value}' is invalid.");
                }
                else if (!WorkflowUpdateValueIsCompatible(field, value))
                {
                    errors.Add(
                        $"Workflow '{workflow.Id}' step '{step.Id}' sets field '{workflow.Entity}.{field.Id}' of type '{field.Type}' to incompatible value '{value}'.");
                }
            }

            if (step.Type.Equals("audit_event", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(step.Event))
                {
                    errors.Add($"Workflow '{workflow.Id}' audit_event step '{step.Id}' must declare event.");
                }
                else if (!EventNamePattern.IsMatch(step.Event))
                {
                    errors.Add($"Workflow '{workflow.Id}' audit_event step '{step.Id}' event must be dotted lower_snake_case.");
                }
            }

            RejectSqlLikeContent(step.Event, $"{path}.event", errors);

            if (step.Then.Count > 0)
            {
                ValidateWorkflowSteps(
                    workflow,
                    step.Then,
                    $"{collectionName}[{index}].then",
                    fieldsByEntity,
                    roleIds,
                    roles,
                    errors,
                    stepIds,
                    isRejectionBranch);
            }
        }
    }

    static bool HasReachableApprovalStep(IReadOnlyList<ErpWorkflowStep> steps) =>
        steps.Any(step =>
            step.Type.Equals("approval", StringComparison.OrdinalIgnoreCase) ||
            step.Type.Equals("condition", StringComparison.OrdinalIgnoreCase) &&
            HasReachableApprovalStep(step.Then));

    static void ValidateTerminalRejectionStepPayload(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        List<string> errors)
    {
        void RejectPayload(bool present, string payload)
        {
            if (present)
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' rejection branch step '{step.Id}' cannot declare '{payload}' for terminal type '{step.Type}'.");
            }
        }

        RejectPayload(!string.IsNullOrEmpty(step.Label), "label");
        RejectPayload(HasAssignmentPayload(step.AssignedTo), "assignedTo");
        RejectPayload(!string.IsNullOrEmpty(step.ApproveLabel), "approveLabel");
        RejectPayload(!string.IsNullOrEmpty(step.RejectLabel), "rejectLabel");
        RejectPayload(step.Condition is not null, "condition");
        RejectPayload(step.If is not null, "if");

        switch (step.Type.Trim().ToLowerInvariant())
        {
            case "update_record":
                RejectPayload(!string.IsNullOrEmpty(step.Event), "event");
                RejectPayload(!string.IsNullOrEmpty(step.Message), "message");
                break;
            case "audit_event":
                RejectPayload(step.Set.Count > 0, "set");
                break;
            case "notification_placeholder":
                RejectPayload(step.Set.Count > 0, "set");
                RejectPayload(!string.IsNullOrEmpty(step.Event), "event");
                break;
        }
    }

    static bool HasAssignmentPayload(ErpWorkflowAssignment? assignment) =>
        assignment is not null &&
        (!string.IsNullOrEmpty(assignment.Type) ||
         !string.IsNullOrEmpty(assignment.Role) ||
         !string.IsNullOrEmpty(assignment.Path) ||
         !string.IsNullOrEmpty(assignment.RequiredRole) ||
         !string.IsNullOrEmpty(assignment.FallbackRole));

    static bool WorkflowUpdateValueIsCompatible(ErpField field, string value) =>
        field.Type.Trim().ToLowerInvariant() switch
        {
            "integer" => long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _),
            "number" or "decimal" or "money" => decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out _),
            "boolean" => bool.TryParse(value, out _),
            "date" => DateOnly.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            "datetime" => DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            "relation" => long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var relationId) && relationId > 0,
            "enum" => field.Options.Contains(value, StringComparer.OrdinalIgnoreCase),
            "string" or "text" => true,
            _ => true
        };

    static void ValidateWorkflowCondition(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        ErpWorkflowCondition condition,
        string path,
        string currentEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var op = condition.Op.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(op))
        {
            errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' must declare op.");
            return;
        }

        if (!WorkflowConditionOps.Contains(op))
        {
            errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' uses unsupported condition op '{condition.Op}'.");
            return;
        }

        RejectSqlLikeContent(condition.Op, $"{path}.op", errors);

        if (op is "and" or "or")
        {
            if (condition.Conditions.Count == 0)
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' op '{op}' must declare conditions.");
                return;
            }

            for (var index = 0; index < condition.Conditions.Count; index++)
            {
                ValidateWorkflowCondition(workflow, step, condition.Conditions[index], $"{path}.conditions[{index}]", currentEntity, fieldsByEntity, errors);
            }

            return;
        }

        var leftField = ValidateWorkflowConditionOperand(workflow, step, condition.Left, $"{path}.left", currentEntity, fieldsByEntity, errors, required: true);
        var rightField = op is "exists" or "not_exists"
            ? null
            : ValidateWorkflowConditionOperand(workflow, step, condition.Right, $"{path}.right", currentEntity, fieldsByEntity, errors, required: true);

        if (op is "exists" or "not_exists")
        {
            return;
        }

        if (op is "gt" or "gte" or "lt" or "lte")
        {
            if (leftField is not null && !NumericFieldTypes.Contains(leftField.Type))
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' op '{op}' requires numeric left field '{leftField.Name}'.");
            }

            if (rightField is not null && !NumericFieldTypes.Contains(rightField.Type))
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' op '{op}' requires numeric right field '{rightField.Name}'.");
            }

            if (condition.Right?.Value is not null && !IsNumericValue(condition.Right.Value))
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' op '{op}' requires a numeric right value.");
            }

            return;
        }

        if (op is "in" or "not_in")
        {
            if (condition.Right?.Value is null || !TryGetArrayValue(condition.Right.Value, out var values))
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' op '{op}' requires right.value to be an array.");
                return;
            }

            if (leftField is not null)
            {
                foreach (var value in values)
                {
                    if (!IsValueCompatible(leftField, value))
                    {
                        errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' op '{op}' contains a value incompatible with field '{leftField.Name}'.");
                        break;
                    }
                }
            }

            return;
        }

        if (leftField is not null && condition.Right?.Value is not null && !IsValueCompatible(leftField, condition.Right.Value))
        {
            errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' compares field '{leftField.Name}' to an incompatible value.");
        }

        if (leftField is not null && rightField is not null && !AreComparableFieldTypes(leftField.Type, rightField.Type))
        {
            errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' compares incompatible fields '{leftField.Name}' and '{rightField.Name}'.");
        }
    }

    static ErpField? ValidateWorkflowConditionOperand(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        ErpWorkflowConditionOperand? operand,
        string path,
        string currentEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors,
        bool required)
    {
        if (operand is null)
        {
            if (required)
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' operand '{path}' is required.");
            }

            return null;
        }

        var fieldSpecified = !string.IsNullOrWhiteSpace(operand.Field);
        var pathSpecified = !string.IsNullOrWhiteSpace(operand.Path);
        var valueSpecified = operand.Value is not null;
        var specifiedCount = new[] { fieldSpecified, pathSpecified, valueSpecified }.Count(specified => specified);
        if (specifiedCount != 1)
        {
            errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' operand '{path}' must declare exactly one of field, path, or value.");
            return null;
        }

        RejectSqlLikeContent(operand.Field, $"{path}.field", errors);
        RejectSqlLikeContent(operand.Path, $"{path}.path", errors);
        RejectSqlLikeValue(operand.Value, $"{path}.value", errors);

        if (fieldSpecified)
        {
            RequireIdentifier(operand.Field, $"{path}.field", errors);
            var field = ResolveField(currentEntity, operand.Field, fieldsByEntity);
            if (field is null)
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' references unknown field '{operand.Field}' on entity '{currentEntity}'.");
            }

            return field;
        }

        if (pathSpecified)
        {
            return ValidateConditionPath(workflow, step, operand.Path, path, currentEntity, fieldsByEntity, errors);
        }

        return null;
    }

    static ErpField? ValidateConditionPath(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        string pathValue,
        string path,
        string startEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var segments = pathValue.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' path is empty.");
            return null;
        }

        var currentEntity = startEntity;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            RequireIdentifier(segment, $"{path}.segment", errors);
            var field = ResolveField(currentEntity, segment, fieldsByEntity);
            if (field is null)
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' path segment '{segment}' is not a field on '{currentEntity}'.");
                return null;
            }

            if (index == segments.Length - 1)
            {
                return field;
            }

            if (!field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(field.References))
            {
                errors.Add($"Workflow '{workflow.Id}' condition step '{step.Id}' path segment '{segment}' must be a relation field.");
                return null;
            }

            currentEntity = field.References!;
        }

        return null;
    }

    static void ValidateWorkflowAssignment(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        string path,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        HashSet<string> roleIds,
        IReadOnlyDictionary<string, ErpRole> roles,
        List<string> errors)
    {
        var assignmentType = string.IsNullOrWhiteSpace(step.AssignedTo.Type)
            ? (string.IsNullOrWhiteSpace(step.AssignedTo.Path) ? "role" : "relation_user")
            : step.AssignedTo.Type.Trim().ToLowerInvariant();

        if (assignmentType != "role" && assignmentType != "relation_user")
        {
            errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' uses unsupported assignedTo.type '{step.AssignedTo.Type}'.");
        }

        RejectSqlLikeContent(step.AssignedTo.Type, $"{path}.assignedTo.type", errors);
        RejectSqlLikeContent(step.AssignedTo.Role, $"{path}.assignedTo.role", errors);
        RejectSqlLikeContent(step.AssignedTo.Path, $"{path}.assignedTo.path", errors);
        RejectSqlLikeContent(step.AssignedTo.RequiredRole, $"{path}.assignedTo.requiredRole", errors);
        RejectSqlLikeContent(step.AssignedTo.FallbackRole, $"{path}.assignedTo.fallbackRole", errors);
        ValidateTaskReviewFields(workflow, step, path, fieldsByEntity, errors);

        if (assignmentType == "role")
        {
            if (step.ReviewFields.Count > 0)
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' approval step '{step.Id}' can declare reviewFields only for an exact relation_user assignment.");
            }

            if (string.IsNullOrWhiteSpace(step.AssignedTo.Role))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' must declare assignedTo.role.");
            }
            else if (RuntimeSystemRoleIds.IsReserved(step.AssignedTo.Role))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' cannot assign reserved system role '{step.AssignedTo.Role}'.");
            }
            else if (!roleIds.Contains(NormalizeRoleId(step.AssignedTo.Role)))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' references unknown role '{step.AssignedTo.Role}'.");
            }

            if (!string.IsNullOrWhiteSpace(step.AssignedTo.RequiredRole))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' role assignment cannot declare assignedTo.requiredRole.");
            }

            ValidateRoleAssignmentAuthority(workflow, step, fieldsByEntity, roles, errors);
        }

        if (assignmentType == "relation_user")
        {
            var relationPath = ValidateRelationAssignmentPath(workflow, step, path, fieldsByEntity, errors);
            if (!string.IsNullOrWhiteSpace(step.AssignedTo.Role))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' relation assignment cannot declare assignedTo.role.");
            }

            if (string.IsNullOrWhiteSpace(step.AssignedTo.RequiredRole))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' relation assignment must declare assignedTo.requiredRole.");
            }
            else if (RuntimeSystemRoleIds.IsReserved(step.AssignedTo.RequiredRole))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' cannot require reserved system role '{step.AssignedTo.RequiredRole}'.");
            }
            else if (!roleIds.Contains(NormalizeRoleId(step.AssignedTo.RequiredRole)))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' references unknown required role '{step.AssignedTo.RequiredRole}'.");
            }

            if (RuntimeSystemRoleIds.IsReserved(step.AssignedTo.FallbackRole))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' cannot assign reserved fallback system role '{step.AssignedTo.FallbackRole}'.");
            }
            else if (!string.IsNullOrWhiteSpace(step.AssignedTo.FallbackRole) &&
                     !roleIds.Contains(NormalizeRoleId(step.AssignedTo.FallbackRole)))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' references unknown fallback role '{step.AssignedTo.FallbackRole}'.");
            }

            ValidateRelationAssignmentAuthority(
                workflow,
                step,
                relationPath,
                roles,
                errors);
        }
    }

    static void ValidateTaskReviewFields(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        string path,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        if (!fieldsByEntity.TryGetValue(workflow.Entity, out var entityFields))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reviewField in step.ReviewFields)
        {
            RequireIdentifier(reviewField, $"{path}.reviewFields", errors);
            RejectSqlLikeContent(reviewField, $"{path}.reviewFields", errors);
            if (!seen.Add(reviewField))
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' approval step '{step.Id}' contains duplicate review field '{reviewField}'.");
                continue;
            }

            var field = UniqueFields(entityFields).FirstOrDefault(candidate =>
                candidate.Id.Equals(reviewField, StringComparison.Ordinal));
            if (field is null || field.Computed is not null)
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' approval step '{step.Id}' review field '{reviewField}' must be one exact declared non-computed field on '{workflow.Entity}'.");
            }
        }
    }

    static ResolvedRelationAssignmentPath? ValidateRelationAssignmentPath(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        string path,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(step.AssignedTo.Path))
        {
            errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' relation assignment must declare assignedTo.path.");
            return null;
        }

        var segments = step.AssignedTo.Path.Split('.', StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' relation assignment path must contain only non-empty relation field segments.");
            return null;
        }

        var currentEntity = workflow.Entity;
        var resolvedFields = new List<ErpField>(segments.Length);
        foreach (var segment in segments)
        {
            RequireIdentifier(segment, $"{path}.assignedTo.path.segment", errors);
            if (!fieldsByEntity.TryGetValue(currentEntity, out var entityFields) ||
                !entityFields.TryGetValue(segment, out var field))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' assignment path segment '{segment}' is not a field on '{currentEntity}'.");
                return null;
            }

            if (!field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(field.References))
            {
                errors.Add($"Workflow '{workflow.Id}' approval step '{step.Id}' assignment path segment '{segment}' must be a relation field.");
                return null;
            }

            resolvedFields.Add(field);
            currentEntity = field.References!;
        }

        return new ResolvedRelationAssignmentPath(resolvedFields);
    }

    static void ValidateRelationAssignmentAuthority(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        ResolvedRelationAssignmentPath? relationPath,
        IReadOnlyDictionary<string, ErpRole> roles,
        List<string> errors)
    {
        var requiredActions = workflow.OnRejected.Count == 0
            ? RequiredRoleAssignmentApprovalActions
            : RequiredRoleAssignmentApprovalAndRejectionActions;
        if (relationPath is not null &&
            TryResolveUnambiguousRole(roles, step.AssignedTo.RequiredRole, out var requiredRole))
        {
            var directRelation = relationPath.Fields.Count == 1
                ? relationPath.Fields[0]
                : null;
            var hasEntityRead = HasExactFlatEntityGrant(requiredRole, workflow.Entity, "read") ||
                directRelation is not null &&
                HasExactDirectRecordGrant(requiredRole, workflow.Entity, "read", directRelation);
            if (!hasEntityRead && step.ReviewFields.Count == 0)
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' approval step '{step.Id}' required role '{step.AssignedTo.RequiredRole}' " +
                    "without entity read must declare a non-empty exact reviewFields contract.");
            }

            ValidateReviewFieldAuthority(
                workflow,
                step,
                requiredRole,
                step.AssignedTo.RequiredRole,
                hasEntityRead,
                "required role",
                errors);

            foreach (var action in requiredActions.Where(action =>
                         !action.Equals("read", StringComparison.Ordinal)))
            {
                var covered = HasExactFlatEntityGrant(requiredRole, workflow.Entity, action) ||
                    directRelation is not null &&
                    !action.Equals("read_task", StringComparison.Ordinal) &&
                    HasExactDirectRecordGrant(requiredRole, workflow.Entity, action, directRelation);
                if (!covered)
                {
                    var authority = directRelation is null
                        ? "an exact flat entity-scope grant; multi-segment relation paths cannot use record-scoped task authority"
                        : action.Equals("read_task", StringComparison.Ordinal)
                            ? "an exact flat entity-scope grant because read_task cannot be record-scoped"
                            : "an exact flat entity-scope grant or the exact direct linked_record scope";
                    errors.Add(
                        $"Workflow '{workflow.Id}' approval step '{step.Id}' required role '{step.AssignedTo.RequiredRole}' " +
                        $"must declare '{workflow.Entity}:{action}' using {authority}.");
                }
            }
        }

        if (TryResolveUnambiguousRole(roles, step.AssignedTo.FallbackRole, out var fallbackRole))
        {
            ValidateReviewFieldAuthority(
                workflow,
                step,
                fallbackRole,
                step.AssignedTo.FallbackRole,
                hasEntityRead: true,
                "fallback role",
                errors);
            foreach (var action in requiredActions)
            {
                if (!HasExactFlatEntityGrant(fallbackRole, workflow.Entity, action))
                {
                    errors.Add(
                        $"Workflow '{workflow.Id}' approval step '{step.Id}' fallback role '{step.AssignedTo.FallbackRole}' " +
                        $"must declare '{workflow.Entity}:{action}' using an exact flat entity-scope grant; " +
                        "record-scoped and wildcard grants cannot supply fallback task authority.");
                }
            }
        }
    }

    static void ValidateReviewFieldAuthority(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        ErpRole role,
        string roleId,
        bool hasEntityRead,
        string roleDescription,
        List<string> errors)
    {
        var hasFieldReadAllowlist = role.Grants.Any(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Field, StringComparison.OrdinalIgnoreCase) &&
            grant.RecordScope is null &&
            grant.Entity.Equals(workflow.Entity, StringComparison.Ordinal) &&
            grant.Action.Equals("read", StringComparison.OrdinalIgnoreCase));
        if (hasEntityRead && !hasFieldReadAllowlist)
        {
            return;
        }

        foreach (var reviewField in step.ReviewFields.Distinct(StringComparer.Ordinal))
        {
            if (!HasExplicitFieldReadGrant(role, workflow.Entity, reviewField))
            {
                errors.Add(
                    $"Workflow '{workflow.Id}' approval step '{step.Id}' {roleDescription} '{roleId}' " +
                    $"must declare exact field read authority for review field '{workflow.Entity}.{reviewField}'.");
            }
        }
    }

    static void ValidateRoleAssignmentAuthority(
        ErpWorkflow workflow,
        ErpWorkflowStep step,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ErpRole> roles,
        List<string> errors)
    {
        if (!TryResolveUnambiguousRole(roles, step.AssignedTo.Role, out var assignedRole))
        {
            return;
        }

        fieldsByEntity.TryGetValue(workflow.Entity, out var workflowEntityFields);
        var requiredActions = workflow.OnRejected.Count == 0
            ? RequiredRoleAssignmentApprovalActions
            : RequiredRoleAssignmentApprovalAndRejectionActions;
        foreach (var action in requiredActions)
        {
            var requiresFlatAuthority =
                action.Equals("read", StringComparison.Ordinal) ||
                action.Equals("read_task", StringComparison.Ordinal);
            var covered = HasExactFlatEntityGrant(assignedRole, workflow.Entity, action) ||
                !requiresFlatAuthority &&
                workflowEntityFields is not null &&
                HasExactDirectRecordGrant(
                    assignedRole,
                    workflow.Entity,
                    action,
                    workflowEntityFields);
            if (covered)
            {
                continue;
            }

            var authority = requiresFlatAuthority
                ? "an exact flat entity-scope grant; record-scoped and wildcard grants cannot supply read or task-discovery authority"
                : "an exact flat entity-scope grant or one exact direct linked_record scope on the workflow entity; wildcard, actor_principal, and indirect linked_record grants cannot supply role-assigned task authority";
            errors.Add(
                $"Workflow '{workflow.Id}' approval step '{step.Id}' assigned role '{step.AssignedTo.Role}' " +
                $"must declare '{workflow.Entity}:{action}' using {authority}.");
        }
    }

    static bool TryResolveUnambiguousRole(
        IReadOnlyDictionary<string, ErpRole> roles,
        string roleId,
        out ErpRole role)
    {
        role = null!;
        if (string.IsNullOrWhiteSpace(roleId))
        {
            return false;
        }

        var normalizedRoleId = NormalizeRoleId(roleId);
        var matches = roles
            .Where(candidate => NormalizeRoleId(candidate.Key).Equals(normalizedRoleId, StringComparison.Ordinal))
            .Select(candidate => candidate.Value)
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        role = matches[0];
        return true;
    }

    static bool HasExactFlatEntityGrant(ErpRole role, string entity, string action) =>
        role.Grants.Any(grant =>
            NormalizeGrantScope(grant.Scope).Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase) &&
            grant.RecordScope is null &&
            string.IsNullOrWhiteSpace(grant.Target) &&
            grant.Entity.Equals(entity, StringComparison.Ordinal) &&
            grant.Action.Equals(action, StringComparison.OrdinalIgnoreCase));

    static bool HasExplicitFieldReadGrant(ErpRole role, string entity, string field) =>
        role.Grants.Any(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Field, StringComparison.OrdinalIgnoreCase) &&
            grant.RecordScope is null &&
            grant.Entity.Equals(entity, StringComparison.Ordinal) &&
            grant.Action.Equals("read", StringComparison.OrdinalIgnoreCase) &&
            grant.Target.Equals(field, StringComparison.Ordinal));

    static bool HasExactDirectRecordGrant(
        ErpRole role,
        string entity,
        string action,
        ErpField directRelation) =>
        role.Grants.Any(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase) &&
            grant.Action.Equals(action, StringComparison.OrdinalIgnoreCase) &&
            grant.Entity.Equals(entity, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(grant.Target) &&
            grant.RecordScope is not null &&
            grant.RecordScope.Field.Equals(directRelation.Id, StringComparison.Ordinal) &&
            grant.RecordScope.Source.Equals(RecordPermissionScopeSources.LinkedRecord, StringComparison.OrdinalIgnoreCase) &&
            grant.RecordScope.LinkedEntity.Equals(directRelation.References, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(grant.RecordScope.LinkedField));

    static bool HasExactDirectRecordGrant(
        ErpRole role,
        string entity,
        string action,
        IReadOnlyDictionary<string, ErpField> workflowEntityFields) =>
        UniqueFields(workflowEntityFields).Any(directRelation =>
            directRelation.Computed is null &&
            directRelation.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(directRelation.References) &&
            HasExactDirectRecordGrant(role, entity, action, directRelation));

    static string NormalizeGrantScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? PermissionGrantScopes.Entity
            : scope;

    // Entity read remains mandatory for ordinary and fallback role queues. A relation_user
    // required role resolves one exact user, so read_task plus decision authority is enough
    // for the task projection; it must not acquire entity-wide CRUD visibility as a side effect.
    static readonly IReadOnlyList<string> RequiredRoleAssignmentApprovalActions =
        ["read", "read_task", "approve_task"];

    static readonly IReadOnlyList<string> RequiredRoleAssignmentApprovalAndRejectionActions =
        ["read", "read_task", "approve_task", "reject_task"];

    static readonly IReadOnlySet<string> TerminalRejectionStepTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "update_record",
            "audit_event",
            "notification_placeholder"
        };

    sealed record ResolvedRelationAssignmentPath(IReadOnlyList<ErpField> Fields);
}
