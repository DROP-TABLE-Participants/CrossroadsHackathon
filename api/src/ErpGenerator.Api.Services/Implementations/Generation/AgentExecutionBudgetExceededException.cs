namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed class AgentExecutionBudgetExceededException(string message)
    : InvalidOperationException(message);
