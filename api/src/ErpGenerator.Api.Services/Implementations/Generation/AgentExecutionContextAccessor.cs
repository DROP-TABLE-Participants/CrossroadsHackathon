namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed class AgentExecutionContextAccessor : IAgentExecutionContextAccessor
{
    public AgentExecutionContext? Current { get; set; }
}
