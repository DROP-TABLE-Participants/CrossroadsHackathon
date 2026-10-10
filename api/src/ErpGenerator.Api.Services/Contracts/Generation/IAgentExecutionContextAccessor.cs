namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IAgentExecutionContextAccessor
{
    AgentExecutionContext? Current { get; set; }
}
