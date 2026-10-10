namespace ErpGenerator.Runtime.Shared.Bundles;

public static class PermissionGrantScopes
{
    public const string Entity = "entity";
    public const string Field = "field";
    public const string Page = "page";
    public const string Action = "action";
    public const string Record = "record";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Entity,
        Field,
        Page,
        Action,
        Record
    };
}

public static class RecordPermissionScopeSources
{
    public const string ActorPrincipal = "actor_principal";
    public const string LinkedRecord = "linked_record";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ActorPrincipal,
        LinkedRecord
    };
}

public static class RecordPermissionActions
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "read",
        "create",
        "update",
        "archive",
        "restore",
        "start_workflow",
        "approve_task",
        "reject_task"
    };
}

public static class GranularPermissionActions
{
    public const string Read = "read";
    public const string Write = "write";
    public const string EffectWrite = "effect_write";
    public const string View = "view";
    public const string Execute = "execute";

    public static readonly IReadOnlySet<string> Field = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Read,
        Write,
        EffectWrite
    };

    public static readonly IReadOnlySet<string> Page = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        View
    };

    public static readonly IReadOnlySet<string> Action = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Execute
    };
}

public static class RuntimePagePermissionKinds
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dashboard",
        "entity_list",
        "entity_create",
        "entity_detail",
        "reports",
        "report_detail"
    };
}
