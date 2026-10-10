using YamlDotNet.Serialization;

namespace ErpGenerator.Runtime.Shared.Bundles;

public sealed class BundleSeedDocument
{
    public BundleSeed Seed { get; init; } = new();

    public List<BundleSeedRecord> Records { get; init; } = [];
}

public sealed class BundleSeed
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;
}

public sealed class BundleSeedRecord
{
    public string Ref { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public Dictionary<string, object?> Values { get; init; } = [];
}

public sealed class BundleAcceptanceTestDocument
{
    /// <summary>
    /// Versioned executable contract schema. Documents written before the
    /// explicit field existed retain the v2 contract and hash behavior.
    /// </summary>
    [YamlMember(Alias = "schema", ApplyNamingConventions = false)]
    public string Schema { get; init; } = BundleAcceptanceTestSchemas.V2;

    public BundleAcceptanceTest Test { get; init; } = new();

    public BundleAcceptanceSetup Setup { get; init; } = new();

    public List<BundleAcceptanceActor> Actors { get; init; } = [];

    public List<BundleAcceptanceStep> Steps { get; init; } = [];

    public List<BundleAcceptanceAssertion> Assertions { get; init; } = [];

    [YamlIgnore]
    public string SourcePath { get; set; } = string.Empty;
}

public static class BundleAcceptanceTestSchemas
{
    public const string V2 = "erpgenerator.bundle-acceptance-test.v2";

    public const string V3 = "erpgenerator.bundle-acceptance-test.v3";

    public static bool IsKnown(string? schema) =>
        string.Equals(schema, V2, StringComparison.Ordinal) ||
        string.Equals(schema, V3, StringComparison.Ordinal);
}

public static class BundleAcceptanceStepTypes
{
    /// <summary>
    /// V3-only, non-mutating harness operation that aliases one exact record
    /// from the hash-bound acceptance seed under the step id.
    /// </summary>
    public const string ReferenceSeedRecord = "reference_seed_record";

    public const string CreateRecord = "create_record";

    public const string RunAction = "run_action";

    /// <summary>
    /// V3-only harness operation that links one exact acceptance actor to a
    /// causally available runtime record. It never produces a record ref.
    /// </summary>
    public const string BindActorRecord = "bind_actor_record";

    /// <summary>
    /// V3-only harness operation that runs one tenant-local scheduler cycle for
    /// one exact compiled schedule and record at a closed due-time boundary.
    /// </summary>
    public const string RunDueSchedule = "run_due_schedule";
}

public static class BundleAcceptanceScheduleTimes
{
    public const string BeforeDue = "before_due";

    public const string Due = "due";

    public static bool IsKnown(string? value) =>
        string.Equals(value, BeforeDue, StringComparison.Ordinal) ||
        string.Equals(value, Due, StringComparison.Ordinal);
}

public sealed class BundleAcceptanceTest
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Identifies whether this contract came from an authored business test or
    /// from the controller's supplemental seed smoke generation. Older bundle
    /// tests predate this field and are treated as authored contracts.
    /// </summary>
    public string Origin { get; init; } = BundleAcceptanceTestOrigins.Authored;

    [YamlMember(Alias = "contract_hash", ApplyNamingConventions = false)]
    public string ContractHash { get; set; } = string.Empty;

    [YamlMember(Alias = "suite_hash", ApplyNamingConventions = false)]
    public string SuiteHash { get; set; } = string.Empty;

    [YamlMember(Alias = "suite_index", ApplyNamingConventions = false)]
    public int? SuiteIndex { get; set; }
}

public static class BundleAcceptanceTestOrigins
{
    public const string Authored = "authored";

    public const string ControllerSmoke = "controller_smoke";

    public static bool IsKnown(string? origin) =>
        string.Equals(origin, Authored, StringComparison.Ordinal) ||
        string.Equals(origin, ControllerSmoke, StringComparison.Ordinal);
}

public sealed class BundleAcceptanceSetup
{
    public string Seed { get; init; } = string.Empty;

    [YamlMember(Alias = "seed_hash", ApplyNamingConventions = false)]
    public string SeedHash { get; init; } = string.Empty;
}

public sealed class BundleAcceptanceActor
{
    public string Id { get; init; } = string.Empty;

    public List<string> Roles { get; init; } = [];
}

public sealed class BundleAcceptanceStep
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public BundleRecordSelector Record { get; init; } = new();

    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// For approve_task and reject_task, identifies the exact preceding
    /// start_workflow_action or due run_due_schedule step whose workflow
    /// instance owns the task.
    /// </summary>
    [YamlMember(Alias = "workflow_ref", ApplyNamingConventions = false)]
    public string WorkflowRef { get; init; } = string.Empty;

    [YamlMember(Alias = "as_role", ApplyNamingConventions = false)]
    public string AsRole { get; init; } = string.Empty;

    [YamlMember(Alias = "as_actor", ApplyNamingConventions = false)]
    public string AsActor { get; init; } = string.Empty;

    public Dictionary<string, object?> Values { get; init; } = [];

    public Dictionary<string, object?> Set { get; init; } = [];

    public string Report { get; init; } = string.Empty;

    /// <summary>
    /// Exact compiled schedule id consumed only by the V3 run_due_schedule
    /// harness operation.
    /// </summary>
    public string Schedule { get; init; } = string.Empty;

    /// <summary>
    /// Closed scheduler-time relation. Only before_due and due are supported;
    /// arbitrary timestamps, offsets, cron expressions, and clock mutation are
    /// deliberately outside the acceptance contract.
    /// </summary>
    [YamlMember(Alias = "schedule_time", ApplyNamingConventions = false)]
    public string ScheduleTime { get; init; } = string.Empty;

    /// <summary>
    /// The stable result contract for this step. Hand-authored bundles that
    /// predate expected outcomes intentionally default to success.
    /// </summary>
    public BundleAcceptanceExpectedOutcome Expected { get; init; } = new();
}

public sealed class BundleAcceptanceExpectedOutcome
{
    public string Outcome { get; init; } = BundleAcceptanceExpectedOutcomes.Success;

    public string Code { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;
}

public static class BundleAcceptanceExpectedOutcomes
{
    public const string Success = "success";
    public const string PermissionDenied = "permission_denied";
    public const string ActionConflict = "action_conflict";
    public const string BusinessRuleRejected = "business_rule_rejected";
    public const string WorkflowRejected = "workflow_rejected";

    public static IReadOnlyList<string> All { get; } =
    [
        Success,
        PermissionDenied,
        ActionConflict,
        BusinessRuleRejected,
        WorkflowRejected
    ];
}

public static class BundleAcceptanceExpectedOutcomeCodes
{
    public const string PermissionEntityCreate = "permission.entity.create";
    public const string PermissionEntityRead = "permission.entity.read";
    public const string PermissionEntityUpdate = "permission.entity.update";
    public const string PermissionEntityArchive = "permission.entity.archive";
    public const string PermissionEntityRestore = "permission.entity.restore";
    public const string PermissionFieldWrite = "permission.field.write";
    public const string PermissionRecordCreate = "permission.record.create";
    public const string PermissionRecordRead = "permission.record.read";
    public const string PermissionRecordUpdate = "permission.record.update";
    public const string PermissionRecordArchive = "permission.record.archive";
    public const string PermissionRecordRestore = "permission.record.restore";
    public const string PermissionRecordStartWorkflow = "permission.record.start_workflow";
    public const string PermissionRecordApproveTask = "permission.record.approve_task";
    public const string PermissionRecordRejectTask = "permission.record.reject_task";
    public const string PermissionNamedActionExecute = "permission.named_action.execute";
    public const string PermissionWorkflowStart = "permission.workflow.start";
    public const string PermissionWorkflowTaskAssignment = "permission.workflow.task_assignment";
    public const string PermissionReportExecute = "permission.report.execute";
    public const string ActionNotAvailable = "action.not_available";
    public const string ActionGuard = "action.guard";
    public const string WorkflowStartGuard = "workflow.start_guard";
    public const string RelationCreateGuard = "relation.create_guard";
    public const string LifecycleNamedActionRequired = "lifecycle.named_action_required";
    public const string WorkflowTaskRejected = "workflow.task_rejected";

    public static IReadOnlyList<string> All { get; } =
    [
        PermissionEntityCreate,
        PermissionEntityRead,
        PermissionEntityUpdate,
        PermissionEntityArchive,
        PermissionEntityRestore,
        PermissionFieldWrite,
        PermissionRecordCreate,
        PermissionRecordRead,
        PermissionRecordUpdate,
        PermissionRecordArchive,
        PermissionRecordRestore,
        PermissionRecordStartWorkflow,
        PermissionRecordApproveTask,
        PermissionRecordRejectTask,
        PermissionNamedActionExecute,
        PermissionWorkflowStart,
        PermissionWorkflowTaskAssignment,
        PermissionReportExecute,
        ActionNotAvailable,
        ActionGuard,
        WorkflowStartGuard,
        RelationCreateGuard,
        LifecycleNamedActionRequired,
        WorkflowTaskRejected
    ];
}

public sealed class BundleAcceptanceAssertion
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// V3 execution checkpoint. "$setup" observes the freshly-applied seed;
    /// every other value identifies the exact step after which this assertion
    /// must execute.
    /// </summary>
    [YamlMember(Alias = "after_step", ApplyNamingConventions = false)]
    public string AfterStep { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public BundleRecordSelector Record { get; init; } = new();

    public string Field { get; init; } = string.Empty;

    public object? Value { get; init; }

    [YamlMember(Alias = "value_ref", ApplyNamingConventions = false)]
    public string ValueRef { get; init; } = string.Empty;

    /// <summary>
    /// Closed discriminator for an intentional null expected value. The only
    /// supported non-empty value is "null" on record_field_equals.
    /// </summary>
    [YamlMember(Alias = "value_kind", ApplyNamingConventions = false)]
    public string ValueKind { get; init; } = string.Empty;

    public int? Count { get; init; }

    public BundleAcceptanceWhere Where { get; init; } = new();

    public string Workflow { get; init; } = string.Empty;

    [YamlMember(Alias = "workflow_ref", ApplyNamingConventions = false)]
    public string WorkflowRef { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    [YamlMember(Alias = "task_step", ApplyNamingConventions = false)]
    public string TaskStep { get; init; } = string.Empty;

    public string Report { get; init; } = string.Empty;

    [YamlMember(Alias = "report_ref", ApplyNamingConventions = false)]
    public string ReportRef { get; init; } = string.Empty;

    public string Metric { get; init; } = string.Empty;

    public string Widget { get; init; } = string.Empty;

    [YamlMember(Alias = "event", ApplyNamingConventions = false)]
    public string Event { get; init; } = string.Empty;

    /// <summary>
    /// Optional v3 correlation to the exact authored workflow audit step that
    /// emitted <see cref="Event"/>. Legacy and non-workflow audit assertions
    /// leave this empty.
    /// </summary>
    [YamlMember(Alias = "audit_step", ApplyNamingConventions = false)]
    public string AuditStep { get; init; } = string.Empty;

    [YamlMember(Alias = "permission_scope", ApplyNamingConventions = false)]
    public string PermissionScope { get; init; } = string.Empty;

    [YamlMember(Alias = "permission_action", ApplyNamingConventions = false)]
    public string PermissionAction { get; init; } = string.Empty;

    [YamlMember(Alias = "permission_role", ApplyNamingConventions = false)]
    public string PermissionRole { get; init; } = string.Empty;

    [YamlMember(Alias = "permission_target", ApplyNamingConventions = false)]
    public string PermissionTarget { get; init; } = string.Empty;
}

public static class BundleAcceptanceAssertionCheckpoints
{
    public const string Setup = "$setup";
}

public sealed class BundleRecordSelector
{
    public string Ref { get; init; } = string.Empty;
}

public sealed class BundleAcceptanceWhere
{
    public string Field { get; init; } = string.Empty;

    public string Op { get; init; } = string.Empty;

    public object? Value { get; init; }

    [YamlMember(Alias = "value_ref", ApplyNamingConventions = false)]
    public string ValueRef { get; init; } = string.Empty;
}
