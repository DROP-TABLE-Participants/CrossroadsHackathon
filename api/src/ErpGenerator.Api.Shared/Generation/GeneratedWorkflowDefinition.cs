namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedWorkflowDefinition(
    string Key,
    string Label,
    string EntityKey,
    string TriggerAction,
    string? ApprovalRole,
    string? StatusField,
    string? ApprovedValue,
    IReadOnlyList<string> Steps);
