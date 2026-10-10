using ErpGenerator.Runtime.Shared.Tenants;

public sealed record RuntimeRouteContext(
    ResolvedTenantEnvironment? Tenant,
    RuntimeSession? Session,
    RuntimeUserContext? User,
    IResult? Error);

public sealed record RuntimeAuthorizationResult(
    RuntimeEntityDefinition? Entity,
    IResult? Error);

public sealed record RuntimeReportAuthorizationResult(
    RuntimeReportDefinition? Report,
    RuntimeEntityDefinition? Entity,
    IResult? Error);

public sealed record RuntimeEntityUiPermissions(
    bool CanRead,
    bool CanCreate,
    bool CanUpdate,
    bool CanArchive,
    bool CanRestore);

public sealed record RuntimeDashboardMetric(
    string Key,
    string Label,
    object? Value,
    string ReportKey);
