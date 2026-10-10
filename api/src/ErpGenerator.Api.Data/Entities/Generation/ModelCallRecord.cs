using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class ModelCallRecord : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    public Guid? GenerationAgentWorkItemId { get; set; }
    public GenerationAgentWorkItem? GenerationAgentWorkItem { get; set; }
    public Guid? GenerationAgentExecutionAttemptId { get; set; }
    public GenerationAgentExecutionAttempt? GenerationAgentExecutionAttempt { get; set; }
    [MaxLength(160)] public string ControllerCallId { get; set; } = string.Empty;
    [MaxLength(120)] public string Phase { get; set; } = string.Empty;
    [MaxLength(64)] public string RequestHash { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = "reserved";
    [MaxLength(200)] public string? ProviderResponseId { get; set; }
    [MaxLength(120)] public string? AgentKey { get; set; }
    [MaxLength(40)] public string? AgentVersion { get; set; }
    [MaxLength(80)] public string? Provider { get; set; }
    [MaxLength(120)] public string? Model { get; set; }
    [MaxLength(40)] public string? PromptVersion { get; set; }
    [MaxLength(40)] public string? ContractVersion { get; set; }
    public long? DurationMilliseconds { get; set; }
    public int? InputTokenCount { get; set; }
    public int? OutputTokenCount { get; set; }
    [MaxLength(64)] public string? ResponseHash { get; set; }
    [MaxLength(160)] public string? FailureCode { get; set; }
    public string? ResultJson { get; set; }
}
