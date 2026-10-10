using ErpGenerator.Runtime.Shared.ArtificialIntelligence;

namespace ErpGenerator.Runtime.Services.Assistant;

public sealed record ErpAssistantAnswer(
    string Answer,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<RuntimeEvidenceReference> Sources)
{
    public Guid InteractionId { get; init; }
}

internal sealed record RuntimeAssistantModelAnswer(
    string Answer,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<string> Sources);
