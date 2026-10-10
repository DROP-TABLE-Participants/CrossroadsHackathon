using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
            input = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = UserPrompt }
            },
            text = new
            {
                format = new { type = "json_schema", name = SchemaName, strict = true, schema = JsonSchema }
            },
            max_output_tokens = Math.Clamp(MaxOutputTokens, 800, 5000),
            store = false
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 30, 120)));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Runtime assistant provider returned status {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException("Runtime assistant provider request failed.", null, response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidOperationException("Runtime assistant response did not complete.");
        foreach (var output in root.GetProperty("output").EnumerateArray())
        {
            if (!output.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) && type.GetString() == "refusal")
                    throw new InvalidOperationException("Runtime assistant declined the request.");
                if (type.GetString() == "output_text" && part.TryGetProperty("text", out var text))
                    return text.GetString() ?? throw new InvalidOperationException("Runtime assistant returned empty output.");
            }
        }
        throw new InvalidOperationException("Runtime assistant returned no structured output.");
    }
}
