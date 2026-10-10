namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record SupervisorDecision(
    string Decision,
    string Reason,
    string? TargetAgentKey = null,
    string? RepairInstructions = null);
