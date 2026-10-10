namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationRunEventFactory
{
    public static GenerationRunEvent Add(
        ErpGeneratorDbContext dbContext,
        GenerationRun run,
        string eventType,
        string summary,
        string? stageKey = null,
        int? attemptNumber = null,
        string safeMetadataJson = "{}")
    {
        run.LastEventSequence =
            Math.Max(
                run.LastEventSequence,
                run.Events
                    .Select(value => value.Sequence)
                    .DefaultIfEmpty()
                    .Max()) + 1;
        var runEvent = new GenerationRunEvent
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            Sequence = run.LastEventSequence,
            EventType = eventType,
            StageKey = stageKey,
            AttemptNumber = attemptNumber,
            Summary = summary.Length <= 1_000 ? summary : summary[..1_000],
            SafeMetadataJson = safeMetadataJson.Length <= 4_000
                ? safeMetadataJson
                : safeMetadataJson[..4_000]
        };
        run.Events.Add(runEvent);
        dbContext.GenerationRunEvents.Add(runEvent);
        return runEvent;
    }
}
