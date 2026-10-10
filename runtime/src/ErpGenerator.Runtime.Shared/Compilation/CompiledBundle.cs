using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Shared.Compilation;

public sealed record CompiledBundle(
    string BundleId,
    string BundleName,
    string Version,
    string BundleHash,
    IReadOnlyList<EntityDefinition> Entities,
    IReadOnlyList<ViewDefinition> Views,
    IReadOnlyList<WorkflowDefinition> Workflows,
    IReadOnlyList<ActionDefinition> Actions,
    IReadOnlyList<RoleDefinition> Roles,
    IReadOnlyList<ReportDefinition> Reports,
    DashboardDefinition Dashboard,
    IReadOnlyList<TablePlan> Tables,
    IReadOnlyList<CompiledGeneratedPrimitive> GeneratedPrimitives)
{
    public IReadOnlyList<ScheduleDefinition> Schedules { get; init; } = [];

    public IReadOnlyList<ExceptionRuleDefinition> ExceptionRules { get; init; } = [];
}

public sealed record ExceptionRuleDefinition(
    string Id,
    string Label,
    string Report,
    string Metric,
    string Comparator,
    decimal Threshold,
    string Severity,
    string Schedule,
    IReadOnlyList<string> RecipientRoles,
    string DeduplicationKey,
    string? ResolutionWorkflow);

public sealed record ScheduleDefinition(
    string Id,
    string Entity,
    string DueField,
    IReadOnlyList<ScheduleFilterDefinition> Filters,
    string Workflow,
    string DefinitionHash);

public sealed record ScheduleFilterDefinition(
    string Field,
    string Op,
    TypedLiteralDefinition? Value,
    IReadOnlyList<TypedLiteralDefinition> Values);

public sealed record CompiledGeneratedPrimitive(
    string InstanceId,
    string TypeKey,
    IReadOnlyDictionary<string, object?> Values);

public sealed record CompiledGeneratedPrimitiveDefinition(
    string Key,
    string Kind,
    IReadOnlyList<CompiledGeneratedPrimitiveMemberDefinition> Members);

public sealed record CompiledGeneratedPrimitiveMemberDefinition(
    string Name,
    string TypeName,
    bool Required,
    string? ConstantValue);

public sealed record EntityDefinition(
    string Id,
    string ModuleId,
    string Name,
    string DisplayName,
    string TableName,
    EntityDisplayDefinition Display,
    IReadOnlyList<FieldDefinition> Fields)
{
    public IReadOnlyList<RelationCreateGuardDefinition> RelationCreateGuards { get; init; } = [];

    public IReadOnlyList<RelationshipDefinition> Relationships { get; init; } = [];

    public IReadOnlyList<EntityCollectionDefinition> Collections { get; init; } = [];
}

public sealed record RelationshipDefinition(
    string Key,
    string SourceEntity,
    string Field,
    string TargetEntity,
    bool Required,
    string Cardinality,
    string? Inverse,
    string ArchiveBehavior);

public sealed record EntityCollectionDefinition(
    string Key,
    string Kind,
    string Label,
    string ChildEntity,
    string ParentRelation,
    string? OrderField,
    IReadOnlyList<string> ShownFields,
    IReadOnlyList<string> EditableFields,
    IReadOnlyList<string> ActionKeys);

public sealed record RelationCreateGuardDefinition(
    string RelationField,
    string ReferencedEntity,
    string ReferencedField,
    string Op,
    TypedLiteralDefinition Value);

public sealed record TypedLiteralDefinition(
    string Type,
    string Value);

public sealed record EntityDisplayDefinition(
    string PrimaryField,
    string? SecondaryField);

public sealed record FieldDefinition(
    string Id,
    string Name,
    string DisplayName,
    string Type,
    string ColumnName,
    string SqlType,
    bool Required,
    bool Unique,
    string? References,
    IReadOnlyList<string> Options,
    ComputedFieldDefinition? Computed,
    FieldLifecycleDefinition? Lifecycle = null);

public sealed record FieldLifecycleDefinition(
    string Initial,
    string Transitions);

public sealed record ComputedFieldDefinition(
    string Op,
    ComputedOperandDefinition Left,
    ComputedOperandDefinition Right);

public sealed record ComputedOperandDefinition(
    string? Field,
    object? Value);

public sealed record ViewDefinition(
    string Id,
    string ModuleId,
    string Name,
    string Entity,
    IReadOnlyList<string> Fields);

public sealed record WorkflowDefinition(
    string Id,
    string ModuleId,
    string Name,
    string Entity,
    IReadOnlyList<string> States,
    WorkflowTriggerDefinition? Trigger,
    IReadOnlyList<WorkflowStepDefinition> Steps,
    IReadOnlyList<WorkflowStepDefinition> OnRejected)
{
    public IReadOnlyList<WorkflowStartGuardDefinition> StartGuards { get; init; } = [];
}

public sealed record WorkflowStartGuardDefinition(
    string Id,
    string Entity,
    IReadOnlyList<ActionFilterDefinition> Where,
    string ResultCountOp,
    int ResultCount,
    string Error);

public sealed record WorkflowTriggerDefinition(
    string Action);

public sealed record WorkflowStepDefinition(
    string Id,
    string Type,
    string Label,
    WorkflowAssignmentDefinition? AssignedTo,
    string? ApproveLabel,
    string? RejectLabel,
    IReadOnlyDictionary<string, string> Set,
    WorkflowConditionDefinition? Condition,
    IReadOnlyList<WorkflowStepDefinition> Then,
    string? Event,
    string? Message,
    IReadOnlyList<string>? ReviewFields = null);

public sealed record WorkflowConditionDefinition(
    string Op,
    WorkflowConditionOperandDefinition? Left,
    WorkflowConditionOperandDefinition? Right,
    IReadOnlyList<WorkflowConditionDefinition> Conditions);

public sealed record WorkflowConditionOperandDefinition(
    string? Field,
    string? Path,
    object? Value);

public sealed record WorkflowAssignmentDefinition(
    string Type,
    string? Role,
    string? Path,
    string? RequiredRole,
    string? FallbackRole);

public sealed record ActionDefinition(
    string Key,
    string Label,
    string Entity,
    string Type,
    string? Description,
    IReadOnlyList<ActionConditionDefinition> VisibleWhen,
    IReadOnlyList<ActionPermissionRequirementDefinition> Permissions,
    IReadOnlyList<ActionStepDefinition> Steps);

public sealed record ActionPermissionRequirementDefinition(
    string Action,
    string Entity);

public sealed record ActionStepDefinition(
    string Id,
    string Type,
    string? Entity,
    IReadOnlyList<ActionFilterDefinition> Where,
    ActionConditionDefinition? Condition,
    string? Error,
    IReadOnlyDictionary<string, ActionValueDefinition> Values,
    ActionForEachDefinition? ForEach,
    ActionRecordsFromDefinition? RecordsFrom,
    IReadOnlyDictionary<string, ActionValueDefinition> Set,
    string? Event,
    string? Message,
    IReadOnlyDictionary<string, ActionValueDefinition> Metadata,
    ActionReturnDefinition? Return);

public sealed record ActionFilterDefinition(
    string Field,
    string Op,
    object? Value,
    ActionValueFromDefinition? ValueFrom,
    IReadOnlyList<object?> Values);

public sealed record ActionConditionDefinition(
    string Op,
    ActionOperandDefinition? Left,
    ActionOperandDefinition? Right,
    IReadOnlyList<ActionConditionDefinition> Conditions);

public sealed record ActionOperandDefinition(
    string? Field,
    object? Value,
    string? ResultCount,
    ActionValueFromDefinition? ValueFrom);

public sealed record ActionValueDefinition(
    object? Value,
    ActionValueFromDefinition? ValueFrom,
    string? Template,
    ActionAggregateDefinition? Aggregate);

public sealed record ActionValueFromDefinition(
    string? Record,
    string? Item,
    string? Step,
    string? Field,
    string? Now);

public sealed record ActionAggregateDefinition(
    string Result,
    string Op,
    ActionExpressionDefinition? Expression);

public sealed record ActionExpressionDefinition(
    string Op,
    ActionOperandDefinition? Left,
    ActionOperandDefinition? Right);

public sealed record ActionForEachDefinition(
    string Result);

public sealed record ActionRecordsFromDefinition(
    string Result);

public sealed record ActionReturnDefinition(
    string? Step,
    string? Entity,
    string? Message);

public sealed record RoleDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<PermissionGrantDefinition> Grants);

public sealed record PermissionGrantDefinition(
    string Action,
    string Entity,
    string Scope = PermissionGrantScopes.Entity,
    string Target = "",
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RecordScopeDefinition? RecordScope = null);

public sealed record RecordScopeDefinition(
    string Field,
    string Source,
    string? LinkedEntity = null,
    string? LinkedField = null);

public sealed record ReportDefinition(
    string Key,
    string Label,
    string? Description,
    string SourceEntity,
    IReadOnlyList<ReportGroupByDefinition> GroupBy,
    IReadOnlyList<ReportMetricDefinition> Metrics,
    IReadOnlyList<ReportFilterDefinition> Filters,
    IReadOnlyList<ReportJoinDefinition>? Joins = null);

public sealed record ReportJoinDefinition(
    string Alias,
    string FromAlias,
    string RelationField,
    string TargetEntity,
    string Type,
    int Depth);

public sealed record ReportGroupByDefinition(
    string Field,
    string Label)
{
    /// <summary>
    /// Compiler-derived identity lineage for a direct relation group output.
    /// Null for ordinary group values and every aggregate metric.
    /// </summary>
    public string? IdentityEntityId { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceValueKind { get; init; }
}

public sealed record ReportMetricDefinition(
    string Key,
    string Label,
    string Op,
    string? Field,
    ReportFilterDefinition? Filter)
{
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceValueKind { get; init; }
}

public sealed record ReportFilterDefinition(
    string Field,
    string Op,
    object? Value,
    IReadOnlyList<object?> Values);

public sealed record DashboardDefinition(
    IReadOnlyList<DashboardWidgetDefinition> Widgets);

public sealed record DashboardWidgetDefinition(
    string Key,
    string Label,
    string Type,
    string Report,
    string Metric);

public sealed record TablePlan(
    string SchemaName,
    string TableName,
    IReadOnlyList<ColumnPlan> Columns,
    IReadOnlyList<IndexPlan> Indexes)
{
    public IReadOnlyList<ForeignKeyPlan> ForeignKeys { get; init; } = [];
}

public sealed record ColumnPlan(
    string Name,
    string SqlType,
    bool IsNullable,
    bool IsSystem = false);

public sealed record IndexPlan(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique);

public sealed record ForeignKeyPlan(
    string Name,
    string Column,
    string ReferencedSchema,
    string ReferencedTable,
    string ReferencedColumn,
    string OnDelete);
