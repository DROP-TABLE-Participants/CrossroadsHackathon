using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace ErpGenerator.Runtime.Shared.Bundles;

public sealed class ErpBundle
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public List<ErpModule> Modules { get; init; } = [];

    public List<ErpReport> Reports { get; init; } = [];

    public ErpDashboard Dashboard { get; init; } = new();

    public List<ErpAction> Actions { get; init; } = [];

    public List<ErpSchedule> Schedules { get; init; } = [];

    [YamlMember(Alias = "exception_rules", ApplyNamingConventions = false)]
    public List<ErpExceptionRule> ExceptionRules { get; init; } = [];

    public Dictionary<string, ErpRole> Roles { get; init; } = [];

    [YamlMember(Alias = "runtime_primitives", ApplyNamingConventions = false)]
    public List<ErpRuntimePrimitive> RuntimePrimitives { get; init; } = [];
}

/// <summary>
/// A bounded one-shot schedule that starts one triggerless workflow when a
/// materialized datetime field on an entity record becomes due.
/// </summary>
public sealed class ErpSchedule
{
    public string Id { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    [YamlMember(Alias = "due_field", ApplyNamingConventions = false)]
    public string DueField { get; init; } = string.Empty;

    public List<ErpScheduleFilter> Filters { get; init; } = [];

    public string Workflow { get; init; } = string.Empty;
}

public sealed class ErpScheduleFilter
{
    public string Field { get; init; } = string.Empty;

    public string Op { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErpTypedLiteral? Value { get; init; }

    public List<ErpTypedLiteral> Values { get; init; } = [];
}

public static class ErpScheduleLimits
{
    public const int MaxFilters = 8;

    public const int MaxValuesPerFilter = 16;
}

public sealed class ErpRuntimePrimitive
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public Dictionary<string, object?> Values { get; init; } = [];
}

public sealed class ErpModule
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public List<ErpEntity> Entities { get; init; } = [];

    public List<ErpView> Views { get; init; } = [];

    public List<ErpWorkflow> Workflows { get; init; } = [];
}

public sealed class ErpEntity
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ErpEntityDisplay Display { get; init; } = new();

    public List<ErpField> Fields { get; init; } = [];

    public List<ErpEntityCollection> Collections { get; init; } = [];

    [YamlMember(Alias = "relation_create_guards", ApplyNamingConventions = false)]
    public List<ErpRelationCreateGuard> RelationCreateGuards { get; init; } = [];
}

public sealed class ErpRelationCreateGuard
{
    [YamlMember(Alias = "relation_field", ApplyNamingConventions = false)]
    public string RelationField { get; init; } = string.Empty;

    [YamlMember(Alias = "referenced_field", ApplyNamingConventions = false)]
    public string ReferencedField { get; init; } = string.Empty;

    public string Op { get; init; } = string.Empty;

    public ErpTypedLiteral Value { get; init; } = new();
}

public sealed class ErpTypedLiteral
{
    public string Type { get; init; } = string.Empty;

    public object? Value { get; init; }
}

public sealed class ErpEntityDisplay
{
    public string Primary { get; init; } = string.Empty;

    public string Secondary { get; init; } = string.Empty;
}

public sealed class ErpField
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public bool Required { get; init; }

    public bool Unique { get; init; }

    public string? References { get; init; }

    public ErpRelationship? Relationship { get; init; }

    public List<string> Options { get; init; } = [];

    public ErpComputedField? Computed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErpFieldLifecycle? Lifecycle { get; init; }
}

public sealed class ErpRelationship
{
    public string Key { get; init; } = string.Empty;

    public string Cardinality { get; init; } = "many_to_one";

    public string? Inverse { get; init; }

    [YamlMember(Alias = "archive_behavior", ApplyNamingConventions = false)]
    public string ArchiveBehavior { get; init; } = "restrict";
}

public sealed class ErpEntityCollection
{
    public string Key { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    [YamlMember(Alias = "child_entity", ApplyNamingConventions = false)]
    public string ChildEntity { get; init; } = string.Empty;

    [YamlMember(Alias = "parent_relation", ApplyNamingConventions = false)]
    public string ParentRelation { get; init; } = string.Empty;

    [YamlMember(Alias = "order_field", ApplyNamingConventions = false)]
    public string? OrderField { get; init; }

    [YamlMember(Alias = "shown_fields", ApplyNamingConventions = false)]
    public List<string> ShownFields { get; init; } = [];

    [YamlMember(Alias = "editable_fields", ApplyNamingConventions = false)]
    public List<string> EditableFields { get; init; } = [];

    [YamlMember(Alias = "action_keys", ApplyNamingConventions = false)]
    public List<string> ActionKeys { get; init; } = [];
}

public sealed class ErpExceptionRule
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Report { get; init; } = string.Empty;

    public string Metric { get; init; } = string.Empty;

    public string Comparator { get; init; } = string.Empty;

    public decimal Threshold { get; init; }

    public string Severity { get; init; } = string.Empty;

    public string Schedule { get; init; } = string.Empty;

    [YamlMember(Alias = "recipient_roles", ApplyNamingConventions = false)]
    public List<string> RecipientRoles { get; init; } = [];

    [YamlMember(Alias = "deduplication_key", ApplyNamingConventions = false)]
    public string DeduplicationKey { get; init; } = string.Empty;

    [YamlMember(Alias = "resolution_workflow", ApplyNamingConventions = false)]
    public string? ResolutionWorkflow { get; init; }
}

public sealed class ErpFieldLifecycle
{
    public string Initial { get; init; } = string.Empty;

    public string Transitions { get; init; } = string.Empty;
}

public sealed class ErpComputedField
{
    public string Op { get; init; } = string.Empty;

    public ErpComputedOperand? Left { get; init; }

    public ErpComputedOperand? Right { get; init; }
}

public sealed class ErpComputedOperand
{
    public string Field { get; init; } = string.Empty;

    public object? Value { get; init; }
}

public sealed class ErpReport
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public List<ErpReportJoin> Joins { get; init; } = [];

    [YamlMember(Alias = "group_by", ApplyNamingConventions = false)]
    public List<ErpReportGroupBy> GroupBy { get; init; } = [];

    public List<ErpReportMetric> Metrics { get; init; } = [];

    public List<ErpReportFilter> Filters { get; init; } = [];
}

public sealed class ErpReportJoin
{
    public string Alias { get; init; } = string.Empty;

    public string From { get; init; } = "source";

    public string Relation { get; init; } = string.Empty;

    public string Type { get; init; } = "left";
}

public sealed class ErpReportGroupBy
{
    public string Field { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;
}

public sealed class ErpReportMetric
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Op { get; init; } = string.Empty;

    public string Field { get; init; } = string.Empty;

    public ErpReportFilter? Filter { get; init; }
}

public sealed class ErpReportFilter
{
    public string Field { get; init; } = string.Empty;

    public string Op { get; init; } = string.Empty;

    public object? Value { get; init; }

    public List<object?> Values { get; init; } = [];
}

public sealed class ErpDashboard
{
    public List<ErpDashboardWidget> Widgets { get; init; } = [];
}

public sealed class ErpDashboardWidget
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Report { get; init; } = string.Empty;

    public string Metric { get; init; } = string.Empty;
}

public sealed class ErpAction
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    [YamlMember(Alias = "visible_when", ApplyNamingConventions = false)]
    public List<ErpActionCondition> VisibleWhen { get; init; } = [];

    public ErpActionRequires Requires { get; init; } = new();

    public List<ErpActionStep> Steps { get; init; } = [];
}

public sealed class ErpActionRequires
{
    public List<ErpGrant> Permissions { get; init; } = [];
}

public sealed class ErpActionStep
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public List<ErpActionFilter> Where { get; init; } = [];

    public ErpActionCondition? Condition { get; init; }

    public string Error { get; init; } = string.Empty;

    public Dictionary<string, ErpActionValue> Values { get; init; } = [];

    [YamlMember(Alias = "foreach")]
    public ErpActionForEach? ForEach { get; init; }

    [YamlMember(Alias = "records_from", ApplyNamingConventions = false)]
    public ErpActionRecordsFrom? RecordsFrom { get; init; }

    public Dictionary<string, ErpActionValue> Set { get; init; } = [];

    public string Event { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public Dictionary<string, ErpActionValue> Metadata { get; init; } = [];

    public ErpActionReturn? Return { get; init; }
}

public sealed class ErpActionFilter
{
    public string Field { get; init; } = string.Empty;

    public string Op { get; init; } = string.Empty;

    public object? Value { get; init; }

    [YamlMember(Alias = "value_from", ApplyNamingConventions = false)]
    public ErpActionValueFrom? ValueFrom { get; init; }

    public List<object?> Values { get; init; } = [];
}

public sealed class ErpActionCondition
{
    public string Op { get; init; } = string.Empty;

    public ErpActionOperand? Left { get; init; }

    public ErpActionOperand? Right { get; init; }

    public List<ErpActionCondition> Conditions { get; init; } = [];
}

public sealed class ErpActionOperand
{
    public string Field { get; init; } = string.Empty;

    public object? Value { get; init; }

    [YamlMember(Alias = "result_count", ApplyNamingConventions = false)]
    public string ResultCount { get; init; } = string.Empty;

    [YamlMember(Alias = "value_from", ApplyNamingConventions = false)]
    public ErpActionValueFrom? ValueFrom { get; init; }
}

public sealed class ErpActionValue
{
    public object? Value { get; init; }

    [YamlMember(Alias = "value_from", ApplyNamingConventions = false)]
    public ErpActionValueFrom? ValueFrom { get; init; }

    public string Template { get; init; } = string.Empty;

    public ErpActionAggregate? Aggregate { get; init; }
}

public sealed class ErpActionValueFrom
{
    public string Record { get; init; } = string.Empty;

    public string Item { get; init; } = string.Empty;

    public string Step { get; init; } = string.Empty;

    public string Field { get; init; } = string.Empty;

    public string Now { get; init; } = string.Empty;
}

public sealed class ErpActionAggregate
{
    public string Result { get; init; } = string.Empty;

    public string Op { get; init; } = string.Empty;

    public ErpActionExpression? Expression { get; init; }
}

public sealed class ErpActionExpression
{
    public string Op { get; init; } = string.Empty;

    public ErpActionOperand? Left { get; init; }

    public ErpActionOperand? Right { get; init; }
}

public sealed class ErpActionForEach
{
    public string Result { get; init; } = string.Empty;
}

public sealed class ErpActionRecordsFrom
{
    public string Result { get; init; } = string.Empty;
}

public sealed class ErpActionReturn
{
    public string Step { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed class ErpView
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public List<string> Fields { get; init; } = [];
}

public sealed class ErpWorkflow
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public List<string> States { get; init; } = [];

    public ErpWorkflowTrigger Trigger { get; init; } = new();

    [YamlMember(Alias = "start_guards", ApplyNamingConventions = false)]
    public List<ErpWorkflowStartGuard> StartGuards { get; init; } = [];

    public List<ErpWorkflowStep> Steps { get; init; } = [];

    public List<ErpWorkflowStep> OnRejected { get; init; } = [];
}

public sealed class ErpWorkflowStartGuard
{
    public string Id { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public List<ErpActionFilter> Where { get; init; } = [];

    [YamlMember(Alias = "result_count_op", ApplyNamingConventions = false)]
    public string ResultCountOp { get; init; } = string.Empty;

    [YamlMember(Alias = "result_count", ApplyNamingConventions = false)]
    public int ResultCount { get; init; }

    public string Error { get; init; } = string.Empty;
}

public static class ErpWorkflowStartGuardLimits
{
    public const int MaxResultCount = 10_000;
}

public sealed class ErpWorkflowTrigger
{
    public string Action { get; init; } = string.Empty;
}

public sealed class ErpWorkflowStep
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public ErpWorkflowAssignment AssignedTo { get; init; } = new();

    public string ApproveLabel { get; init; } = string.Empty;

    public string RejectLabel { get; init; } = string.Empty;

    public List<string> ReviewFields { get; init; } = [];

    public Dictionary<string, string> Set { get; init; } = [];

    public ErpWorkflowCondition? Condition { get; init; }

    [YamlMember(Alias = "if")]
    public ErpWorkflowCondition? If { get; init; }

    public List<ErpWorkflowStep> Then { get; init; } = [];

    public string Event { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed class ErpWorkflowCondition
{
    public string Op { get; init; } = string.Empty;

    public ErpWorkflowConditionOperand? Left { get; init; }

    public ErpWorkflowConditionOperand? Right { get; init; }

    public List<ErpWorkflowCondition> Conditions { get; init; } = [];
}

public sealed class ErpWorkflowConditionOperand
{
    public string Field { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public object? Value { get; init; }
}

public sealed class ErpWorkflowAssignment
{
    public string Type { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string RequiredRole { get; init; } = string.Empty;

    public string FallbackRole { get; init; } = string.Empty;
}

public sealed class ErpRole
{
    public string DisplayName { get; init; } = string.Empty;

    public List<ErpGrant> Grants { get; init; } = [];
}

public sealed class ErpGrant
{
    public string Action { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public string Scope { get; init; } = PermissionGrantScopes.Entity;

    public string Target { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErpRecordScope? RecordScope { get; init; }
}

public sealed class ErpRecordScope
{
    public string Field { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string LinkedEntity { get; init; } = string.Empty;

    public string LinkedField { get; init; } = string.Empty;
}
