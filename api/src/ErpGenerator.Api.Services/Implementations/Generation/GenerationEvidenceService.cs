using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class GenerationEvidenceService(
    ErpGeneratorDbContext dbContext,
    INangoInputSourceService nango) : IGenerationEvidenceService
{
    public async Task CaptureAsync(
        Guid generationRunId,
        bool refreshConnectedSources,
        CancellationToken cancellationToken)
    {
        var ownership = await dbContext.GenerationRuns.AsNoTracking()
            .Where(value => value.Id == generationRunId)
            .Select(value => new { value.GenerationProjectId, value.GenerationProject.CustomerId })
            .SingleAsync(cancellationToken);
        if (refreshConnectedSources)
            await nango.ImportConnectedEvidenceAsync(ownership.CustomerId, ownership.GenerationProjectId, cancellationToken);

        var run = await dbContext.GenerationRuns
            .AsSplitQuery()
            .Include(value => value.EvidenceSnapshots)
            .Include(value => value.GenerationProject).ThenInclude(value => value.InputDocuments)
            .Include(value => value.GenerationProject).ThenInclude(value => value.InputSources).ThenInclude(value => value.SyncedRecords)
            .Include(value => value.GenerationProject).ThenInclude(value => value.InterviewRequests).ThenInclude(value => value.Answers)
            .Include(value => value.PendingWorkItems)
            .SingleAsync(value => value.Id == generationRunId, cancellationToken);
        var entries = BuildEntries(run);
        var text = string.Join("\n\n", entries.Select(value => value.Text));
        if (text.Length > 160_000) text = text[..160_000];
        var hash = Sha256(text);
        var current = run.EvidenceSnapshots.OrderByDescending(value => value.Sequence).FirstOrDefault();
        if (current?.ContentSha256 == hash) return;

        var snapshot = new GenerationEvidenceSnapshot
        {
            GenerationRun = run,
            Sequence = (current?.Sequence ?? 0) + 1,
            EvidenceText = text,
            ContentSha256 = hash,
            ItemCount = entries.Count
        };
        foreach (var entry in entries)
        {
            snapshot.Items.Add(new GenerationEvidenceSnapshotItem
            {
                GenerationEvidenceSnapshot = snapshot,
                SourceType = entry.SourceType,
                SourceEntityId = entry.SourceEntityId,
                ExternalId = entry.ExternalId,
                Title = entry.Title,
                ContentSha256 = Sha256(entry.Text)
            });
        }
        dbContext.GenerationEvidenceSnapshots.Add(snapshot);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static List<EvidenceEntry> BuildEntries(GenerationRun run)
    {
        var project = run.GenerationProject;
        var entries = new List<EvidenceEntry>();
        var profile = new StringBuilder()
            .AppendLine($"Company: {project.CompanyName}")
            .AppendLine($"Industry: {project.Industry}")
            .AppendLine($"Company type: {project.CompanyType}")
            .AppendLine($"ERP goal: {project.Goal}")
            .AppendLine($"Discovery mode: {project.DiscoveryMode}")
            .ToString().Trim();
        entries.Add(new EvidenceEntry("project_profile", project.Id, null, "Company profile", profile));
        entries.AddRange(project.InputDocuments.OrderBy(value => value.CreatedAt).Select(value =>
            new EvidenceEntry("customer_evidence", value.Id, null, value.Title,
                $"Evidence document: {value.Title}\n{value.Content}")));
        entries.AddRange(project.InputSources.OrderBy(value => value.CreatedAt)
            .SelectMany(value => value.SyncedRecords)
            .Where(value => value.ContentState == "content_extracted" && !string.IsNullOrWhiteSpace(value.Content))
            .OrderBy(value => value.CreatedAt)
            .Select(value => new EvidenceEntry(
                value.RecordModel == "UnifiedMessage" ? "nango_message" : "nango_document",
                value.Id,
                value.ExternalId,
                value.Title,
                $"Connected-source evidence ({value.Provider}): {value.Title}\n{value.Content}")));
        entries.AddRange(project.InterviewRequests.SelectMany(value => value.Answers)
            .OrderBy(value => value.CreatedAt)
            .Select(value => new EvidenceEntry("interview_answer", value.Id, value.QuestionKey,
                value.QuestionKey, $"Interview answer ({value.QuestionKey}):\n{value.AnswerText}")));
        entries.AddRange(run.PendingWorkItems
            .Where(value => value.Status == "resolved" && IsCustomerResolution(value.ResolutionNote))
            .OrderBy(value => value.ResolvedAt ?? value.UpdatedAt)
            .Select(value => new EvidenceEntry(
                "customer_resolution",
                value.Id,
                value.Key,
                value.Key,
                $"Customer answer to '{value.Prompt}':\n{value.ResolutionNote}")));
        return entries;
    }

    private static bool IsCustomerResolution(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "Evidence document submitted by the customer."
            and not "Linked interview completed."
            and not "Customer approved generation with explicit assumptions.";

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

}
