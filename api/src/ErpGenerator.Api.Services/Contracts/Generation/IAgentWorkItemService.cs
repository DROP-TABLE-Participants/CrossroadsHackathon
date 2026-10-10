namespace ErpGenerator.Api.Services.Contracts.Generation;

internal interface IAgentWorkItemService
{
    Task<AgentWorkItemExecution> StartAsync(
        GenerationRun run,
        GenerationRunStage stage,
        GenerationEvidenceSnapshot evidenceSnapshot,
        AgentDefinition definition,
        string inputManifestHash,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        AgentWorkItemExecution execution,
        GenerationEvidenceSnapshot evidenceSnapshot,
        GenerationStageResult result,
        CancellationToken cancellationToken);

    Task<SupervisorDecision> FailAsync(
        AgentWorkItemExecution execution,
        GenerationEvidenceSnapshot evidenceSnapshot,
        GenerationFailure failure,
        CancellationToken cancellationToken);
}
