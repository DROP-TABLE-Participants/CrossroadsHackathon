namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed class AgentDefinitionRegistry : IAgentDefinitionRegistry
{
    private readonly IReadOnlyDictionary<string, AgentDefinition> byKey;
    private readonly IReadOnlyDictionary<string, AgentDefinition> byStage;

    public AgentDefinitionRegistry()
    {
        var definitions = BuildDefinitions();
        Validate(definitions);
        All = definitions;
        byKey = definitions.ToDictionary(value => value.Key, StringComparer.Ordinal);
        byStage = definitions
            .Where(value => value.StageKey is not null)
            .ToDictionary(value => value.StageKey!, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<AgentDefinition> All { get; }

    public AgentDefinition Get(string agentKey) =>
        byKey.TryGetValue(agentKey, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Agent '{agentKey}' is not registered.");

    public AgentDefinition GetForStage(string stageKey) =>
        byStage.TryGetValue(stageKey, out var definition)
            ? definition
            : throw new KeyNotFoundException(
                $"No agent is registered for generation stage '{stageKey}'.");

    private static AgentDefinition[] BuildDefinitions() =>
    [
        new(
            "company_understanding_agent",
            "1.0",
            "company_understanding",
            "discovery",
            "company_understanding",
            "evidence_snapshot",
            "company_understanding",
            "erp_discovery_assessment",
            "1.0",
            "1.0",
            "company_understanding_supervisor",
            [],
            [],
            3,
            2,
            2,
            false),
        new(
            "company_understanding_supervisor",
            "1.0",
            "supervisor",
            "discovery",
            null,
            "company_understanding",
            "supervisor_decision",
            "supervisor_decision",
            "1.0",
            "1.0",
            null,
            ["company_understanding_agent"],
            ["company_understanding_agent"],
            1,
            0,
            2,
            true),
        new(
            "erp_design_agent",
            "1.0",
            "erp_design",
            "design",
            "erp_design",
            "company_understanding",
            "erp_bundle",
            "generated_erp_bundle",
            "1.0",
            "1.0",
            "erp_design_supervisor",
            ["company_understanding_agent"],
            [],
            3,
            2,
            2,
            false),
        new(
            "erp_design_supervisor",
            "1.0",
            "supervisor",
            "design",
            null,
            "erp_bundle",
            "supervisor_decision",
            "supervisor_decision",
            "1.0",
            "1.0",
            null,
            ["erp_design_agent"],
            ["erp_design_agent", "company_understanding_agent"],
            1,
            0,
            2,
            true)
    ];

    private static void Validate(IReadOnlyCollection<AgentDefinition> definitions)
    {
        if (definitions.Count == 0)
            throw new InvalidOperationException("At least one agent definition is required.");
        var duplicateKey = definitions.GroupBy(value => value.Key, StringComparer.Ordinal)
            .FirstOrDefault(value => value.Count() > 1);
        if (duplicateKey is not null)
            throw new InvalidOperationException(
                $"Agent key '{duplicateKey.Key}' is registered more than once.");
        var duplicateStage = definitions.Where(value => value.StageKey is not null)
            .GroupBy(value => value.StageKey!, StringComparer.Ordinal)
            .FirstOrDefault(value => value.Count() > 1);
        if (duplicateStage is not null)
            throw new InvalidOperationException(
                $"Generation stage '{duplicateStage.Key}' has more than one primary agent.");

        var byKey = definitions.ToDictionary(value => value.Key, StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Key) ||
                string.IsNullOrWhiteSpace(definition.Version) ||
                string.IsNullOrWhiteSpace(definition.InputArtifactType) ||
                string.IsNullOrWhiteSpace(definition.OutputArtifactType) ||
                definition.MaximumAttempts < 1 ||
                definition.MaximumRepairs < 0 ||
                definition.MaximumRoutingDepth < 0)
            {
                throw new InvalidOperationException(
                    $"Agent definition '{definition.Key}' is incomplete.");
            }
            if (definition.SupervisorAgentKey is not null &&
                (!byKey.TryGetValue(definition.SupervisorAgentKey, out var supervisor) ||
                 !supervisor.IsSupervisor))
            {
                throw new InvalidOperationException(
                    $"Agent '{definition.Key}' references an invalid supervisor.");
            }
            foreach (var route in definition.AllowedRouteAgentKeys)
            {
                if (!byKey.ContainsKey(route))
                    throw new InvalidOperationException(
                        $"Agent '{definition.Key}' routes to unknown agent '{route}'.");
            }
            foreach (var dependency in definition.DependsOnAgentKeys)
            {
                if (!byKey.ContainsKey(dependency))
                    throw new InvalidOperationException(
                        $"Agent '{definition.Key}' depends on unknown agent '{dependency}'.");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
            Visit(definition.Key, byKey, visiting, visited);
    }

    private static void Visit(
        string key,
        IReadOnlyDictionary<string, AgentDefinition> definitions,
        ISet<string> visiting,
        ISet<string> visited)
    {
        if (visited.Contains(key)) return;
        if (!visiting.Add(key))
            throw new InvalidOperationException(
                $"Agent dependency graph contains a cycle at '{key}'.");
        foreach (var dependency in definitions[key].DependsOnAgentKeys)
            Visit(dependency, definitions, visiting, visited);
        visiting.Remove(key);
        visited.Add(key);
    }
}
