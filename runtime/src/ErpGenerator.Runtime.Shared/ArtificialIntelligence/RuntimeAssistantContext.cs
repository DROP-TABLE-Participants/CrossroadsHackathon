namespace ErpGenerator.Runtime.Shared.ArtificialIntelligence;

public sealed record RuntimeAssistantContext(
    string Kind,
    string Route,
    string? EntityKey = null,
    long? RecordId = null,
    string? ReportKey = null,
    long? TaskId = null,
    long? WorkflowInstanceId = null)
{
    public string BundleHash { get; init; } = string.Empty;
}

public static class RuntimeAssistantContextKinds
{
    public const string Dashboard = "dashboard";
    public const string EntityList = "entity_list";
    public const string RecordDetail = "record_detail";
    public const string ReportDetail = "report_detail";
    public const string TaskDetail = "task_detail";
    public const string WorkflowInstance = "workflow_instance";

    public static IReadOnlySet<string> Supported { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Dashboard,
            EntityList,
            RecordDetail,
            ReportDetail,
            TaskDetail,
            WorkflowInstance
        };
}
