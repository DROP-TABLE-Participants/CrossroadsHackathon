namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentWorkItemExecution(
    GenerationAgentWorkItem WorkItem,
    GenerationAgentExecutionAttempt Attempt,
    AgentDefinition Definition);
