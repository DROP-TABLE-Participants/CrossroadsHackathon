using System.Text.Json;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Compiler-backed proof identity for acceptance record references. Ordinary
/// action execution may have many effects, but an action can back a test ref
/// only when it explicitly returns its one exact created record.
/// </summary>
public static class BundleAcceptanceRecordProducerContract
{
    public static bool TryResolveProducedEntity(
        BundleAcceptanceStep producer,
        CompiledBundle compiled,
        out string entity)
    {
        entity = string.Empty;
        if (!BundleAcceptanceExpectedOutcomeContract.IsSuccess(producer))
        {
            return false;
        }

        if (producer.Type.Equals(BundleAcceptanceStepTypes.CreateRecord, StringComparison.OrdinalIgnoreCase))
        {
            if (compiled.Entities.Count(candidate => candidate.Id.Equals(producer.Entity, StringComparison.Ordinal)) != 1)
            {
                return false;
            }

            entity = producer.Entity;
            return true;
        }

        if (producer.Type.Equals(BundleAcceptanceStepTypes.ReferenceSeedRecord, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(producer.Record?.Ref) ||
                compiled.Entities.Count(candidate => candidate.Id.Equals(producer.Entity, StringComparison.Ordinal)) != 1)
            {
                return false;
            }

            entity = producer.Entity;
            return true;
        }

        if (!producer.Type.Equals(BundleAcceptanceStepTypes.RunAction, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actions = compiled.Actions.Where(action =>
                action.Entity.Equals(producer.Entity, StringComparison.Ordinal) &&
                action.Key.Equals(producer.Action, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (actions is not [{ } action] || !TryResolveActionProducedEntity(action, out entity))
        {
            return false;
        }

        var resolvedEntity = entity;
        return compiled.Entities.Count(candidate => candidate.Id.Equals(resolvedEntity, StringComparison.Ordinal)) == 1;
    }

    public static bool TryResolveActionProducedEntity(
        ActionDefinition action,
        out string entity)
    {
        entity = string.Empty;
        if (action.Steps.Any(step => step.Type.Equals("create_records_for_each", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var creates = action.Steps
            .Select((step, index) => (Step: step, Index: index))
            .Where(item => item.Step.Type.Equals("create_record", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var returns = action.Steps
            .Select((step, index) => (Step: step, Index: index))
            .Where(item => item.Step.Type.Equals("return_result", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (creates is not [{ Step: { } create, Index: var createIndex }] ||
            returns is not [{ Step: { Return: { } returned }, Index: var returnIndex }] ||
            returnIndex <= createIndex ||
            string.IsNullOrWhiteSpace(create.Entity) ||
            !string.Equals(returned.Step, create.Id, StringComparison.Ordinal) ||
            !string.Equals(returned.Entity, create.Entity, StringComparison.Ordinal))
        {
            return false;
        }

        entity = create.Entity!;
        return true;
    }

    public static string? FindUnsupportedReason(
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        CompiledBundle compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        foreach (var test in tests)
        {
            var stepsById = test.Steps
                .Where(step => !string.IsNullOrWhiteSpace(step.Id))
                .GroupBy(step => step.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var referencedEntities = new List<(string Reference, string? ExpectedEntity, string Consumer)>();
            foreach (var step in test.Steps)
            {
                if (!string.IsNullOrWhiteSpace(step.Record?.Ref))
                {
                    referencedEntities.Add((step.Record.Ref, step.Entity, $"step '{step.Id}' record_ref"));
                }

                AddValueReferences(compiled, step.Entity, step.Values, step.Id, referencedEntities);
                AddValueReferences(compiled, step.Entity, step.Set, step.Id, referencedEntities);
            }

            foreach (var assertion in test.Assertions)
            {
                if (!string.IsNullOrWhiteSpace(assertion.Record?.Ref))
                {
                    referencedEntities.Add((
                        assertion.Record.Ref,
                        assertion.Entity,
                        $"assertion '{assertion.Id}' record_ref"));
                }

                if (!string.IsNullOrWhiteSpace(assertion.ValueRef))
                {
                    referencedEntities.Add((
                        assertion.ValueRef,
                        ExpectedAssertionValueEntity(compiled, assertion),
                        $"assertion '{assertion.Id}' value_ref"));
                }

                if (!string.IsNullOrWhiteSpace(assertion.Where?.ValueRef))
                {
                    referencedEntities.Add((
                        assertion.Where.ValueRef,
                        ExpectedWhereValueEntity(compiled, assertion),
                        $"assertion '{assertion.Id}' where.value_ref"));
                }
            }

            foreach (var (reference, expectedEntity, consumer) in referencedEntities)
            {
                if (!stepsById.TryGetValue(reference, out var producer) ||
                    producer.Type is null ||
                    !(producer.Type.Equals(BundleAcceptanceStepTypes.ReferenceSeedRecord, StringComparison.OrdinalIgnoreCase) ||
                      producer.Type.Equals(BundleAcceptanceStepTypes.CreateRecord, StringComparison.OrdinalIgnoreCase) ||
                      producer.Type.Equals(BundleAcceptanceStepTypes.RunAction, StringComparison.OrdinalIgnoreCase)))
                {
                    // Seed refs are not action-produced proof identities and are
                    // validated by the seed/runtime isolation boundary.
                    continue;
                }

                if (!TryResolveProducedEntity(producer, compiled, out var producedEntity))
                {
                    return $"Acceptance test '{test.Test.Key}' {consumer} references run_action producer '{reference}' without one exact explicitly returned created record.";
                }

                if (!string.IsNullOrWhiteSpace(expectedEntity) &&
                    !producedEntity.Equals(expectedEntity, StringComparison.Ordinal))
                {
                    return $"Acceptance test '{test.Test.Key}' {consumer} expects entity '{expectedEntity}' but producer '{reference}' returns '{producedEntity}'.";
                }
            }

            foreach (var assertion in test.Assertions.Where(assertion =>
                         assertion.Type.Equals("workflow_instance_count", StringComparison.OrdinalIgnoreCase)))
            {
                var workflows = compiled.Workflows.Where(workflow =>
                        workflow.Id.Equals(assertion.Workflow, StringComparison.Ordinal) &&
                        workflow.Entity.Equals(assertion.Entity, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (workflows is not [{ } exactWorkflow])
                {
                    return $"Acceptance workflow_instance_count assertion '{assertion.Id}' must reference one exact compiled workflow for entity '{assertion.Entity}'.";
                }

                if (!stepsById.TryGetValue(assertion.Record?.Ref ?? string.Empty, out var producer) ||
                    !TryResolveProducedEntity(producer, compiled, out var producedEntity) ||
                    !producedEntity.Equals(assertion.Entity, StringComparison.Ordinal))
                {
                    return $"Acceptance workflow_instance_count assertion '{assertion.Id}' record_ref must resolve to one exact produced '{assertion.Entity}' record.";
                }

                if (!stepsById.TryGetValue(assertion.AfterStep, out var checkpoint))
                {
                    return $"Acceptance workflow_instance_count assertion '{assertion.Id}' must use its exact workflow-producing step as after_step.";
                }

                var checkpointMatches = checkpoint.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase)
                    ? compiled.Workflows.Count(workflow =>
                          workflow.Id.Equals(exactWorkflow.Id, StringComparison.Ordinal) &&
                          workflow.Entity.Equals(checkpoint.Entity, StringComparison.Ordinal) &&
                          workflow.Trigger?.Action.Equals(checkpoint.Action, StringComparison.Ordinal) == true) == 1 &&
                      string.IsNullOrWhiteSpace(assertion.WorkflowRef)
                    : checkpoint.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase) &&
                      (checkpoint.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.Due, StringComparison.Ordinal) ||
                       checkpoint.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.BeforeDue, StringComparison.Ordinal) &&
                       assertion.Count == 0) &&
                      assertion.WorkflowRef.Equals(checkpoint.Id, StringComparison.Ordinal) &&
                      compiled.Schedules.Count(schedule =>
                          schedule.Id.Equals(checkpoint.Schedule, StringComparison.Ordinal) &&
                          schedule.Entity.Equals(checkpoint.Entity, StringComparison.Ordinal) &&
                          schedule.Workflow.Equals(exactWorkflow.Id, StringComparison.Ordinal)) == 1;
                if (!checkpointMatches ||
                    !checkpoint.Entity.Equals(assertion.Entity, StringComparison.Ordinal) ||
                    !string.Equals(checkpoint.Record?.Ref, assertion.Record?.Ref, StringComparison.Ordinal))
                {
                    return checkpoint.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase)
                        ? $"Acceptance workflow_instance_count assertion '{assertion.Id}' must bind its start_workflow_action checkpoint to that exact compiled workflow, entity, record ref, and trigger action."
                        : $"Acceptance workflow_instance_count assertion '{assertion.Id}' must bind its run_due_schedule checkpoint to that exact compiled schedule, workflow, entity, and record ref.";
                }
            }

            foreach (var assertion in test.Assertions.Where(assertion =>
                         !string.IsNullOrWhiteSpace(assertion.WorkflowRef) &&
                         IsWorkflowReferenceAssertion(assertion.Type)))
            {
                if (!stepsById.TryGetValue(assertion.WorkflowRef, out var checkpoint) ||
                    !checkpoint.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var schedules = compiled.Schedules.Where(schedule =>
                        schedule.Id.Equals(checkpoint.Schedule, StringComparison.Ordinal) &&
                        schedule.Entity.Equals(checkpoint.Entity, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (schedules is not [{ } schedule] ||
                    !checkpoint.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.Due, StringComparison.Ordinal) ||
                    !checkpoint.Entity.Equals(assertion.Entity, StringComparison.Ordinal) ||
                    !string.Equals(checkpoint.Record?.Ref, assertion.Record?.Ref, StringComparison.Ordinal) ||
                    !string.IsNullOrWhiteSpace(assertion.Workflow) &&
                    !schedule.Workflow.Equals(assertion.Workflow, StringComparison.Ordinal))
                {
                    return $"Acceptance assertion '{assertion.Id}' scheduled workflow_ref must bind one exact due schedule, workflow, entity, and record ref.";
                }
            }

            foreach (var taskStep in test.Steps.Where(step =>
                         IsWorkflowTaskDecisionStep(step.Type) &&
                         !string.IsNullOrWhiteSpace(step.WorkflowRef)))
            {
                if (!stepsById.TryGetValue(taskStep.WorkflowRef, out var checkpoint) ||
                    !checkpoint.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var schedules = compiled.Schedules.Where(schedule =>
                        schedule.Id.Equals(checkpoint.Schedule, StringComparison.Ordinal) &&
                        schedule.Entity.Equals(checkpoint.Entity, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (schedules is not [{ } schedule] ||
                    !checkpoint.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.Due, StringComparison.Ordinal) ||
                    !checkpoint.Entity.Equals(taskStep.Entity, StringComparison.Ordinal) ||
                    !string.Equals(checkpoint.Record?.Ref, taskStep.Record?.Ref, StringComparison.Ordinal) ||
                    compiled.Workflows.Count(workflow =>
                        workflow.Id.Equals(schedule.Workflow, StringComparison.Ordinal) &&
                        workflow.Entity.Equals(checkpoint.Entity, StringComparison.Ordinal)) != 1)
                {
                    return $"Acceptance task step '{taskStep.Id}' scheduled workflow_ref must bind one exact due schedule, workflow, entity, and record ref.";
                }
            }

            foreach (var step in test.Steps.Where(step =>
                         step.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase)))
            {
                var schedules = compiled.Schedules.Where(schedule =>
                        schedule.Id.Equals(step.Schedule, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (schedules is not [{ } schedule] ||
                    !schedule.Entity.Equals(step.Entity, StringComparison.Ordinal))
                {
                    return $"Acceptance run_due_schedule step '{step.Id}' must reference one exact compiled schedule for entity '{step.Entity}'.";
                }

                if (!BundleAcceptanceScheduleTimes.IsKnown(step.ScheduleTime))
                {
                    return $"Acceptance run_due_schedule step '{step.Id}' uses unsupported schedule_time '{step.ScheduleTime}'.";
                }
            }
        }

        return null;
    }

    private static void AddValueReferences(
        CompiledBundle compiled,
        string entityId,
        IReadOnlyDictionary<string, object?> values,
        string stepId,
        ICollection<(string Reference, string? ExpectedEntity, string Consumer)> references)
    {
        var entity = compiled.Entities.SingleOrDefault(candidate => candidate.Id.Equals(entityId, StringComparison.Ordinal));
        foreach (var (fieldId, value) in values)
        {
            var expectedEntity = entity?.Fields.SingleOrDefault(field =>
                field.Id.Equals(fieldId, StringComparison.Ordinal))?.References;
            foreach (var reference in EnumerateRecordRefs(value))
            {
                references.Add((reference, expectedEntity, $"step '{stepId}' field '{fieldId}' $ref"));
            }
        }
    }

    private static bool IsWorkflowReferenceAssertion(string type) =>
        type.Equals("workflow_status", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("task_exists", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("task_count", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("notification_exists", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkflowTaskDecisionStep(string type) =>
        type.Equals("approve_task", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("reject_task", StringComparison.OrdinalIgnoreCase);

    private static string? ExpectedAssertionValueEntity(
        CompiledBundle compiled,
        BundleAcceptanceAssertion assertion)
    {
        if (!assertion.Type.Equals("record_field_equals", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return compiled.Entities.SingleOrDefault(entity => entity.Id.Equals(assertion.Entity, StringComparison.Ordinal))?
            .Fields.SingleOrDefault(field => field.Id.Equals(assertion.Field, StringComparison.Ordinal))?
            .References;
    }

    private static string? ExpectedWhereValueEntity(
        CompiledBundle compiled,
        BundleAcceptanceAssertion assertion) =>
        compiled.Entities.SingleOrDefault(entity => entity.Id.Equals(assertion.Entity, StringComparison.Ordinal))?
            .Fields.SingleOrDefault(field => field.Id.Equals(assertion.Where.Field, StringComparison.Ordinal))?
            .References;

    private static IEnumerable<string> EnumerateRecordRefs(object? value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("ref", out var reference) &&
                reference.ValueKind == JsonValueKind.String &&
                reference.GetString() is { Length: > 0 } text)
            {
                yield return text;
            }

            yield break;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly &&
            readOnly.TryGetValue("ref", out var readOnlyReference) &&
            Convert.ToString(readOnlyReference, System.Globalization.CultureInfo.InvariantCulture) is { Length: > 0 } readOnlyText)
        {
            yield return readOnlyText;
            yield break;
        }

        if (value is IDictionary<string, object?> dictionary &&
            dictionary.TryGetValue("ref", out var dictionaryReference) &&
            Convert.ToString(dictionaryReference, System.Globalization.CultureInfo.InvariantCulture) is { Length: > 0 } dictionaryText)
        {
            yield return dictionaryText;
        }
    }
}
