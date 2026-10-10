namespace ErpGenerator.Runtime.Shared.ArtificialIntelligence;

public sealed record RuntimeEvidenceReference(
    string Id,
    string Kind,
    string Label,
    string Url,
    DateTimeOffset ObservedAt,
    string BundleHash,
    string ResultHash)
{
    public string? EntityKey { get; init; }

    public long? RecordId { get; init; }

    public string? ReportKey { get; init; }

    public long? TaskId { get; init; }

    public long? WorkflowInstanceId { get; init; }

    public long? AuditEventId { get; init; }
}

public static class RuntimeEvidenceKinds
{
    public const string Record = "record";
    public const string RelatedRecord = "related_record";
    public const string Report = "report";
    public const string Task = "task";
    public const string WorkflowInstance = "workflow_instance";
    public const string WorkflowEvent = "workflow_event";
    public const string AuditEvent = "audit_event";
    public const string RecordCollection = "record_collection";
    public const string OperationalSummary = "operational_summary";
}
