namespace ErpGenerator.Api.Tests;

internal static class TestGenerationArtifactFactory
{
    public static GenerationArtifactVersion Create(
        GenerationRun run,
        string payloadJson,
        string contentHash)
    {
        var stage = new GenerationRunStage
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            StageKey = "erp_design",
            Status = "completed",
            Sequence = 20,
            AttemptCount = 1,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var workItem = new GenerationAgentWorkItem
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            GenerationRunStage = stage,
            GenerationRunStageId = stage.Id,
            WorkItemKey = "erp_design:primary",
            AgentKey = "erp_design_agent",
            AgentVersion = "1.0",
            AgentRole = "erp_design",
            AgentLayer = "design",
            ContractVersion = "1.0",
            DefinitionHash = new string('d', 64),
            Status = "completed",
            InputManifestHash = new string('i', 64)
        };
        var attempt = new GenerationAgentExecutionAttempt
        {
            GenerationAgentWorkItem = workItem,
            GenerationAgentWorkItemId = workItem.Id,
            AttemptNumber = 1,
            Status = "completed",
            WorkerId = "test-worker",
            LeaseId = Guid.NewGuid(),
            AgentKey = workItem.AgentKey,
            AgentVersion = workItem.AgentVersion,
            DefinitionHash = workItem.DefinitionHash,
            InputManifestHash = workItem.InputManifestHash,
            InputEnvelopeJson = "{}",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        return new GenerationArtifactVersion
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            GenerationRunStage = stage,
            GenerationRunStageId = stage.Id,
            GenerationAgentWorkItem = workItem,
            GenerationAgentWorkItemId = workItem.Id,
            GenerationAgentExecutionAttempt = attempt,
            GenerationAgentExecutionAttemptId = attempt.Id,
            ArtifactKey = "erp_bundle",
            ArtifactType = "erp_bundle",
            SchemaName = "generated_erp_bundle",
            SchemaVersion = "1.0",
            Version = 1,
            ContentHash = contentHash,
            InputManifestHash = workItem.InputManifestHash,
            EvidenceSnapshotHash = new string('e', 64),
            ProducerAgentKey = workItem.AgentKey,
            ProducerAgentVersion = workItem.AgentVersion,
            ProducerDefinitionHash = workItem.DefinitionHash,
            CanonicalJson =
                $$"""{"agentKey":"erp_design_agent","agentVersion":"1.0","contractVersion":"1.0","inputManifestHash":"{{workItem.InputManifestHash}}","payload":{{payloadJson}},"evidence":[],"assumptions":[],"uncertainties":[],"warnings":[],"proposedRoutes":[]}""",
            ValidationStatus = "valid"
        };
    }
}
