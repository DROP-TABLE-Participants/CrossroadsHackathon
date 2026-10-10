using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Runtime.Services.Assistant;

public sealed class RuntimeClaudeClient(
    HttpClient httpClient,
    IOptions<RuntimeAssistantOptions> options,
    ILogger<RuntimeClaudeClient> logger) : IRuntimeAssistantModelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> GenerateStructuredAsync(
        string SystemPrompt,
        string UserPrompt,
        string SchemaName,
        JsonElement JsonSchema,
        int MaxOutputTokens,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Provider.Equals("Claude", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new RuntimeAssistantConfigurationException();
        }
        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey)
            ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            : settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) throw new RuntimeAssistantConfigurationException();

        var payload = new
        {
            model = settings.Model,
            system = $"{SystemPrompt}\nReturn JSON conforming to the {SchemaName} contract.",
            messages = new[]
            {
                new { role = "user", content = UserPrompt }
            },
            output_config = new
            {
                format = new { type = "json_schema", schema = ProviderSchema(JsonSchema) }
            },
            max_tokens = Math.Clamp(MaxOutputTokens, 800, 5000)
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 30, 120)));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", apiKey.Trim());
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Runtime assistant provider returned status {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException("Runtime assistant provider request failed.", null, response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var stopReason = root.TryGetProperty("stop_reason", out var reason) ? reason.GetString() : null;
        if (stopReason != "end_turn")
            throw new InvalidOperationException($"Runtime assistant response did not complete (stop_reason: {stopReason ?? "missing"}).");
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Runtime assistant returned no structured output.");
        var outputText = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                part.TryGetProperty("text", out var text))
                outputText.Append(text.GetString());
        }
        if (outputText.Length == 0)
            throw new InvalidOperationException("Runtime assistant returned no structured output.");
        var result = outputText.ToString();
        using var structured = JsonDocument.Parse(result);
        ValidateArrayLimits(JsonSchema, structured.RootElement);
        return result;
    }

    private static JsonNode ProviderSchema(JsonElement schema)
    {
        var result = JsonNode.Parse(schema.GetRawText())!;
        Adapt(result);
        return result;

        static void Adapt(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["type"]?.ToString() == "array" && obj.Remove("maxItems", out var maximum))
                    obj["description"] = $"{obj["description"]} Return at most {maximum} items.".Trim();
                foreach (var property in obj.ToArray()) Adapt(property.Value);
            }
            else if (node is JsonArray array)
                foreach (var child in array) Adapt(child);
        }
    }

    private static void ValidateArrayLimits(JsonElement schema, JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("maxItems", out var maximum) && output.GetArrayLength() > maximum.GetInt32())
                throw new JsonException("Runtime assistant exceeded the original array limit.");
            if (schema.TryGetProperty("minItems", out var minimum) && output.GetArrayLength() < minimum.GetInt32())
                throw new JsonException("Runtime assistant did not meet the original array minimum.");
            if (schema.TryGetProperty("items", out var items))
                foreach (var item in output.EnumerateArray()) ValidateArrayLimits(items, item);
        }
        else if (output.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
                if (output.TryGetProperty(property.Name, out var value)) ValidateArrayLimits(property.Value, value);
    }
}
