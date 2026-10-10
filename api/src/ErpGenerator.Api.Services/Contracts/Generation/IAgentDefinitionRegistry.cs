namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IAgentDefinitionRegistry
{
    IReadOnlyCollection<AgentDefinition> All { get; }
    AgentDefinition Get(string agentKey);
    AgentDefinition GetForStage(string stageKey);
}
