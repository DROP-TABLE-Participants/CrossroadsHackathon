using System.Text.Json;

namespace ErpGenerator.Runtime.Services.Assistant;

public interface IRuntimeAssistantModelClient
{
    Task<string> GenerateStructuredAsync(
        string SystemPrompt,
        string UserPrompt,
        string SchemaName,
        JsonElement JsonSchema,
        int MaxOutputTokens,
        CancellationToken cancellationToken);
}
