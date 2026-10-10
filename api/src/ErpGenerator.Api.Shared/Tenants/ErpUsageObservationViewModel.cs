namespace ErpGenerator.Api.Shared.Tenants;

public sealed record ErpUsageObservationViewModel(
    Guid Id,
    Guid TenantId,
    Guid TenantEnvironmentId,
    string SourceEnvironment,
    string Status,
    string Kind,
    string Severity,
    string Title,
    string Summary,
    int EvidenceCount,
    string? RelatedEntityKey,
    string? RelatedReportKey,
    string? RelatedWorkflowKey,
    DateTimeOffset CreatedAt);
