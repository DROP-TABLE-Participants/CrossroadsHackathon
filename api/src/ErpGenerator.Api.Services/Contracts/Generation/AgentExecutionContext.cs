namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentExecutionContext(
    Guid WorkItemId,
    Guid AttemptId,
    string AgentKey,
    string AgentVersion,
    string PromptVersion,
    string ContractVersion);
