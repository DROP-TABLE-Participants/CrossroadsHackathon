namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentProposedRoute(
    string Decision,
    string? TargetAgentKey,
    string Reason);
