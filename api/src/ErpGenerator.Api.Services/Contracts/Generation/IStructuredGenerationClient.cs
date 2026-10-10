using System.Text.Json;

namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IStructuredGenerationClient
{
    Task<T> GenerateAsync<T>(
        Guid runId,
        string phase,
        string systemPrompt,
        string userPrompt,
        string schemaName,
        JsonElement schema,
        CancellationToken cancellationToken);
}
