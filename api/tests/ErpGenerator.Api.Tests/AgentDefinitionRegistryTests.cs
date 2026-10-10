namespace ErpGenerator.Api.Tests;

public sealed class AgentDefinitionRegistryTests
{
    [Fact]
    public void Registry_defines_only_the_existing_operations_and_their_supervisors()
    {
        var registry = new AgentDefinitionRegistry();

        Assert.Equal(4, registry.All.Count);
        Assert.Equal(
            "company_understanding_agent",
            registry.GetForStage("company_understanding").Key);
        Assert.Equal(
            "erp_design_agent",
            registry.GetForStage("erp_design").Key);
        Assert.Equal(
            "company_understanding_supervisor",
            registry.Get("company_understanding_agent").SupervisorAgentKey);
        Assert.Equal(
            "erp_design_supervisor",
            registry.Get("erp_design_agent").SupervisorAgentKey);
        Assert.Equal(
            ["company_understanding_agent"],
            registry.Get("erp_design_agent").DependsOnAgentKeys);
        Assert.All(registry.All, definition =>
        {
            Assert.Equal(64, definition.DefinitionHash.Length);
            Assert.Equal(definition.DefinitionHash, definition.DefinitionHash);
        });
    }
}
