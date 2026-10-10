using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class ClaudeGenerationClient(
    HttpClient httpClient,
    ErpGeneratorDbContext dbContext,
    IAgentExecutionContextAccessor executionContextAccessor,
    IOptions<ClaudeGenerationOptions> options,
    ILogger<ClaudeGenerationClient> logger) : IStructuredGenerationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T> GenerateAsync<T>(
        Guid runId,
        string phase,
        string systemPrompt,
        string userPrompt,
        string schemaName,
        JsonElement schema,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Claude generation is disabled. Set Claude__Provider=Claude.");
        var model = settings.Model?.Trim();
        if (string.IsNullOrEmpty(model)) throw new InvalidOperationException("Claude__Model is required.");
        var apiKey = settings.ApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey)) apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")?.Trim();
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Claude__ApiKey or ANTHROPIC_API_KEY is required.");
        if (settings.Effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new InvalidOperationException("Claude__Effort must be low, medium, high, xhigh, or max.");
        if (!settings.Endpoint.IsAbsoluteUri ||
            (settings.Endpoint.Scheme != Uri.UriSchemeHttps && !settings.Endpoint.IsLoopback))
            throw new InvalidOperationException("Claude__Endpoint must use HTTPS, except for a local test provider.");

        var payload = new
        {
            model,
            system = $"{systemPrompt}\nReturn JSON conforming to the {schemaName} contract.",
            messages = new[] { new { role = "user", content = userPrompt } },
            max_tokens = Math.Clamp(settings.MaxOutputTokens, 1_000, 128_000),
            stream = true,
            output_config = new
            {
                effort = settings.Effort,
                format = new { type = "json_schema", schema = ClaudeStructuredOutputSchema.ForProvider(schema) }
            }
        };
        var requestHash = Hash(JsonSerializer.Serialize(new
        {
            endpoint = settings.Endpoint.ToString(), settings.ApiVersion, payload, originalSchema = schema
        }, JsonOptions));
        var controllerCallId = BuildControllerCallId(runId, phase, requestHash);
        var call = await dbContext.ModelCallRecords.SingleOrDefaultAsync(
            value => value.GenerationRunId == runId && value.ControllerCallId == controllerCallId,
            cancellationToken);
        if (call is not null && call.RequestHash != requestHash)
            throw new InvalidOperationException("A durable model call cannot be reused with different input.");
        if (call?.Status == "completed" && !string.IsNullOrWhiteSpace(call.ResultJson))
            return Deserialize<T>(call.ResultJson);

        if (call is null)
        {
            var execution = executionContextAccessor.Current;
            call = new ModelCallRecord
            {
                GenerationRunId = runId,
                GenerationAgentWorkItemId = execution?.WorkItemId,
                GenerationAgentExecutionAttemptId = execution?.AttemptId,
                ControllerCallId = controllerCallId,
                Phase = phase,
                RequestHash = requestHash,
                Status = "reserved",
                AgentKey = execution?.AgentKey,
                AgentVersion = execution?.AgentVersion,
                Provider = "Claude",
                Model = model,
                PromptVersion = execution?.PromptVersion,
                ContractVersion = execution?.ContractVersion
            };
            dbContext.ModelCallRecords.Add(call);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
                settings.TimeoutSeconds, 10, (int)GenerationTiming.MaximumModelCallDuration.TotalSeconds)));
            // Messages cannot be retrieved later. Only locally completed calls are replayable.
            call.Status = "processing";
            call.FailureCode = null;
            call.ProviderResponseId = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            var resultJson = await PostAsync(apiKey, settings, controllerCallId, payload, call, deadline.Token);
            using var result = JsonDocument.Parse(resultJson);
            ClaudeStructuredOutputSchema.ValidateArrayLimits(schema, result.RootElement);
            var typedResult = Deserialize<T>(resultJson);
            call.ResultJson = resultJson;
            call.Status = "completed";
            call.ResponseHash = Hash(resultJson);
            call.DurationMilliseconds = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            await dbContext.SaveChangesAsync(cancellationToken);
            return typedResult;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            call.Status = "failed";
            call.FailureCode = exception switch
            {
                TimeoutException or OperationCanceledException => "timeout",
                HttpRequestException => "provider_http_failure",
                EndOfStreamException => "provider_stream_interrupted",
                JsonException => "malformed_output",
                _ => "model_call_failed"
            };
            call.DurationMilliseconds = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<string> PostAsync(
        string apiKey, ClaudeGenerationOptions settings, string clientRequestId, object payload,
        ModelCallRecord call, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", settings.ApiVersion);
        // Correlation only; unfinished requests are not deduplicated by this header.
        request.Headers.Add("x-client-request-id", clientRequestId);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Claude generation request failed with status {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException($"Claude generation request failed with status {(int)response.StatusCode}.", null, response.StatusCode);
        }
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            return await ReadStreamAsync(response.Content, call, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        call.ProviderResponseId = RequiredString(root, "id");
        ReadUsage(root, call);
        EnsureComplete(RequiredString(root, "stop_reason"));
        var text = new StringBuilder();
        foreach (var block in root.GetProperty("content").EnumerateArray())
            if (RequiredString(block, "type") == "text") text.Append(RequiredString(block, "text"));
        return RequireOutput(text);
    }

    private static async Task<string> ReadStreamAsync(HttpContent content, ModelCallRecord call, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var eventData = new StringBuilder();
        var text = new StringBuilder();
        var textBlocks = new HashSet<int>();
        string? stopReason = null;
        var messageStopped = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart());
            }
            else if (line.Length == 0 && eventData.Length > 0)
            {
                using var document = JsonDocument.Parse(eventData.ToString());
                eventData.Clear();
                var root = document.RootElement;
                switch (RequiredString(root, "type"))
                {
                    case "message_start":
                        var message = root.GetProperty("message");
                        call.ProviderResponseId = RequiredString(message, "id");
                        ReadUsage(message, call);
                        break;
                    case "content_block_start":
                        var block = root.GetProperty("content_block");
                        if (RequiredString(block, "type") == "text")
                        {
                            textBlocks.Add(root.GetProperty("index").GetInt32());
                            text.Append(RequiredString(block, "text"));
                        }
                        break;
                    case "content_block_delta":
                        var delta = root.GetProperty("delta");
                        if (RequiredString(delta, "type") == "text_delta" && textBlocks.Contains(root.GetProperty("index").GetInt32()))
                            text.Append(RequiredString(delta, "text"));
                        break;
                    case "message_delta":
                        var messageDelta = root.GetProperty("delta");
                        if (messageDelta.TryGetProperty("stop_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                            stopReason = reason.GetString();
                        ReadUsage(root, call);
                        break;
                    case "message_stop":
                        messageStopped = true;
                        break;
                    case "error":
                        var errorType = RequiredString(root.GetProperty("error"), "type");
                        var status = errorType switch
                        {
                            "overloaded_error" => (HttpStatusCode)529,
                            "rate_limit_error" => HttpStatusCode.TooManyRequests,
                            "invalid_request_error" => HttpStatusCode.BadRequest,
                            "authentication_error" => HttpStatusCode.Unauthorized,
                            "permission_error" => HttpStatusCode.Forbidden,
                            _ => HttpStatusCode.InternalServerError
                        };
                        throw new HttpRequestException($"Claude stream failed ({errorType}).", null, status);
                }
                if (messageStopped) break;
            }
        }
        if (!messageStopped || string.IsNullOrWhiteSpace(call.ProviderResponseId))
            throw new EndOfStreamException("Claude stream ended before message_stop.");
        EnsureComplete(stopReason);
        return RequireOutput(text);
    }

    private static void EnsureComplete(string? stopReason)
    {
        if (stopReason != "end_turn")
            throw new InvalidOperationException($"Claude generation did not complete successfully (stop_reason: {stopReason ?? "missing"}).");
    }

    private static string RequireOutput(StringBuilder text) => text.Length > 0
        ? text.ToString() : throw new InvalidOperationException("Claude response did not contain structured output text.");

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : throw new InvalidOperationException($"Claude response did not contain '{name}'.");

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("Claude returned an empty structured response.");

    private static string BuildControllerCallId(Guid runId, string phase, string requestHash)
    {
        var readablePhase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase.Trim();
        if (readablePhase.Length > 48) readablePhase = readablePhase[..48];
        return $"{runId:N}:{readablePhase}:{requestHash}";
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ReadUsage(JsonElement root, ModelCallRecord call)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
        if (usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inputTokens))
            call.InputTokenCount = inputTokens + TokenCount(usage, "cache_creation_input_tokens") + TokenCount(usage, "cache_read_input_tokens");
        if (usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var outputTokens))
            call.OutputTokenCount = outputTokens;
    }

    private static int TokenCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;
}
