using ErpGenerator.Runtime.Shared.ArtificialIntelligence;

namespace ErpGenerator.Runtime.Services.Assistant;

public static class RuntimeAssistantEvidenceValidator
{
    public static IReadOnlyList<RuntimeEvidenceReference> Resolve(
        IReadOnlyList<string> sourceIds,
        IReadOnlyDictionary<string, RuntimeEvidenceReference> authorizedEvidence,
        string bundleHash)
    {
        if (sourceIds.Count is < 1 or > 8)
        {
            throw new InvalidOperationException(
                "The ERP assistant must cite between one and eight authorized evidence references.");
        }

        var result = new List<RuntimeEvidenceReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceId in sourceIds)
        {
            if (!seen.Add(sourceId))
            {
                continue;
            }

            if (!authorizedEvidence.TryGetValue(sourceId, out var evidence) ||
                !evidence.Id.Equals(sourceId, StringComparison.Ordinal) ||
                !evidence.BundleHash.Equals(bundleHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The ERP assistant returned evidence that was not part of the authorized bundle snapshot.");
            }

            result.Add(evidence);
        }

        return result;
    }
}
