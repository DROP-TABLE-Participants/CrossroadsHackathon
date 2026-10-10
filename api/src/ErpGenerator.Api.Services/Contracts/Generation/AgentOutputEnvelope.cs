using System.Text.Json;

namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record AgentOutputEnvelope(
    string AgentKey,
    string AgentVersion,
    string ContractVersion,
    string InputManifestHash,
    JsonElement Payload,
    IReadOnlyList<AgentEvidenceReference> Evidence,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Uncertainties,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AgentProposedRoute> ProposedRoutes);
