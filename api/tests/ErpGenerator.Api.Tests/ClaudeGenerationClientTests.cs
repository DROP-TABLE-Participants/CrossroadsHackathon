using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Tests;

public sealed class ClaudeGenerationClientTests
{
    [Fact]
    public async Task Changed_evidence_uses_a_new_durable_call_while_exact_retries_reuse_the_existing_result()
    {
        await using var dbContext = new ErpGeneratorDbContext(
            new DbContextOptionsBuilder<ErpGeneratorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var handler = new CompletedResponseHandler();
        var executionAccessor = new AgentExecutionContextAccessor
        {
            Current = new AgentExecutionContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "company_understanding_agent",
                "1.0",
                "1.0",
                "1.0")
        };
        var client = new ClaudeGenerationClient(
            new HttpClient(handler),
            dbContext,
            executionAccessor,
            Options.Create(new ClaudeGenerationOptions
            {
                Provider = "Claude",
                Model = "claude-opus-5-5",
                ApiKey = "test-key",
                Endpoint = new Uri("https://api.example.test/v1/messages"),
                TimeoutSeconds = 30
            }),
            NullLogger<ClaudeGenerationClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}},"required":["ok"]}
            """);
        var runId = Guid.NewGuid();

        var first = await client.GenerateAsync<Dictionary<string, bool>>(
            runId, "discovery_assessment", "system", "evidence-one", "test", schemaDocument.RootElement,
            CancellationToken.None);
        var replay = await client.GenerateAsync<Dictionary<string, bool>>(
            runId, "discovery_assessment", "system", "evidence-one", "test", schemaDocument.RootElement,
            CancellationToken.None);
        var resumed = await client.GenerateAsync<Dictionary<string, bool>>(
            runId, "discovery_assessment", "system", "evidence-two", "test", schemaDocument.RootElement,
            CancellationToken.None);

        Assert.True(first["ok"]);
        Assert.True(replay["ok"]);
        Assert.True(resumed["ok"]);
        Assert.Equal(2, handler.RequestCount);
        var calls = await dbContext.ModelCallRecords.OrderBy(value => value.CreatedAt).ToListAsync();
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, calls.Select(value => value.ControllerCallId).Distinct().Count());
        Assert.All(calls, value => Assert.InRange(value.ControllerCallId.Length, 1, 160));
        Assert.All(calls, value =>
        {
            Assert.Equal(executionAccessor.Current.WorkItemId, value.GenerationAgentWorkItemId);
            Assert.Equal(executionAccessor.Current.AttemptId, value.GenerationAgentExecutionAttemptId);
            Assert.Equal("company_understanding_agent", value.AgentKey);
            Assert.Equal("claude-opus-5-5", value.Model);
            Assert.Equal("Claude", value.Provider);
            Assert.Equal("completed", value.Status);
            Assert.Equal(12, value.InputTokenCount);
            Assert.Equal(20, value.OutputTokenCount);
            Assert.Equal(64, value.ResponseHash?.Length);
            Assert.NotNull(value.DurationMilliseconds);
        });
        Assert.Equal(
            calls.Select(value => value.ControllerCallId).Order(),
            handler.ClientRequestIds.Order());
    }

    private sealed class CompletedResponseHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> ClientRequestIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.True(request.Headers.TryGetValues("x-client-request-id", out var values));
            ClientRequestIds.Add(Assert.Single(values));
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = payload.RootElement;
            Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
            Assert.StartsWith("system", root.GetProperty("system").GetString());
            Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal(30_000, root.GetProperty("max_tokens").GetInt32());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.False(root.TryGetProperty("tool_choice", out _));
            Assert.False(root.TryGetProperty("thinking", out _));
            Assert.False(root.TryGetProperty("background", out _));
            var body = $$"""
                {
                  "id": "response-{{RequestCount}}",
                  "type": "message",
                  "role": "assistant",
                  "stop_reason": "end_turn",
                  "content": [
                    { "type": "thinking", "thinking": "", "signature": "test-signature" },
                    { "type": "text", "text": "{\"ok\":true}" }
                  ],
                  "usage": {"input_tokens":7,"cache_creation_input_tokens":3,"cache_read_input_tokens":2,"output_tokens":20}
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("refusal")]
    [InlineData("tool_use")]
    [InlineData("pause_turn")]
    public async Task Non_successful_stop_reasons_never_become_completed_artifacts(string reason)
    {
        await using var db = NewDatabase();
        var client = Client(db, new ResponseHandler(Reply(reason)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(client));
        Assert.Contains(reason, error.Message);
        var call = Assert.Single(db.ModelCallRecords);
        Assert.Equal("failed", call.Status);
        Assert.Null(call.ResultJson);
        Assert.Equal(20, call.OutputTokenCount);
    }

    [Fact]
    public async Task Malformed_JSON_is_retained_as_a_failed_call()
    {
        await using var db = NewDatabase();
        var client = Client(db, new ResponseHandler(Reply("end_turn", "not JSON")));
        await Assert.ThrowsAnyAsync<JsonException>(() => Generate(client));
        Assert.Equal("malformed_output", Assert.Single(db.ModelCallRecords).FailureCode);
    }

    [Fact]
    public async Task HTTP_failures_preserve_status_for_the_existing_recovery_classifier()
    {
        await using var db = NewDatabase();
        var client = Client(db, new ResponseHandler("{}", status: HttpStatusCode.TooManyRequests));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Generate(client));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal("generation_provider_unavailable", GenerationFailureClassifier.Classify(error).Code);
        Assert.Equal("provider_http_failure", Assert.Single(db.ModelCallRecords).FailureCode);
    }

    [Fact]
    public async Task Streaming_ignores_thinking_and_uses_final_cumulative_usage()
    {
        await using var db = NewDatabase();
        var client = Client(db, new ResponseHandler(StreamedReply(), "text/event-stream"));
        var result = await Generate(client);
        Assert.True(result["ok"]);
        var call = Assert.Single(db.ModelCallRecords);
        Assert.Equal("completed", call.Status);
        Assert.Equal("msg-stream", call.ProviderResponseId);
        Assert.Equal(12, call.InputTokenCount);
        Assert.Equal(20, call.OutputTokenCount);
        Assert.Equal("{\"ok\":true}", call.ResultJson);
    }

    [Fact]
    public async Task Interrupted_streams_never_publish_partial_JSON()
    {
        await using var db = NewDatabase();
        var body = StreamedReply().Replace("data: {\"type\":\"message_stop\"}\n\n", "");
        var client = Client(db, new ResponseHandler(body, "text/event-stream"));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Generate(client));
        var call = Assert.Single(db.ModelCallRecords);
        Assert.Equal("provider_stream_interrupted", call.FailureCode);
        Assert.Null(call.ResultJson);
    }

    [Fact]
    public async Task Overload_events_in_a_successful_HTTP_stream_are_retryable_failures()
    {
        await using var db = NewDatabase();
        var client = Client(db, new ResponseHandler(
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}\n\n", "text/event-stream"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Generate(client));
        Assert.Equal(529, (int)error.StatusCode!);
        Assert.Equal("generation_provider_unavailable", GenerationFailureClassifier.Classify(error).Code);
        Assert.Equal("failed", Assert.Single(db.ModelCallRecords).Status);
    }

    [Fact]
    public async Task Original_array_bounds_remain_enforced_after_provider_schema_adaptation()
    {
        await using var db = NewDatabase();
        using var schema = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{"values":{"type":"array","maxItems":1,"items":{"type":"string"}}},"required":["values"]}
            """);
        var adapted = ClaudeStructuredOutputSchema.ForProvider(schema.RootElement);
        Assert.Null(adapted["properties"]!["values"]!["maxItems"]);
        Assert.Contains("at most 1", adapted["properties"]!["values"]!["description"]!.ToString());
        Assert.Equal(1, schema.RootElement.GetProperty("properties").GetProperty("values").GetProperty("maxItems").GetInt32());
        var client = Client(db, new ResponseHandler(Reply("end_turn", "{\"values\":[\"one\",\"two\"]}")));
        await Assert.ThrowsAsync<JsonException>(() => client.GenerateAsync<JsonElement>(
            Guid.NewGuid(), "test", "system", "evidence", "test", schema.RootElement, CancellationToken.None));
        Assert.Equal("failed", Assert.Single(db.ModelCallRecords).Status);
    }

    private static ErpGeneratorDbContext NewDatabase() => new(
        new DbContextOptionsBuilder<ErpGeneratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static ClaudeGenerationClient Client(ErpGeneratorDbContext db, HttpMessageHandler handler) => new(
        new HttpClient(handler), db, new AgentExecutionContextAccessor(),
        Options.Create(new ClaudeGenerationOptions { ApiKey = "test-key" }),
        NullLogger<ClaudeGenerationClient>.Instance);

    private static async Task<Dictionary<string, bool>> Generate(ClaudeGenerationClient client)
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}},"required":["ok"]}
            """);
        return await client.GenerateAsync<Dictionary<string, bool>>(
            Guid.NewGuid(), "test", "system", "evidence", "test", schema.RootElement, CancellationToken.None);
    }

    private static string Reply(string stopReason, string result = "{\"ok\":true}") => JsonSerializer.Serialize(new
    {
        id = "msg-test", type = "message", stop_reason = stopReason,
        content = new[] { new { type = "text", text = result } },
        usage = new { input_tokens = 12, output_tokens = 20 }
    });

    private static string StreamedReply() =>
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-stream\",\"usage\":{\"input_tokens\":7,\"cache_creation_input_tokens\":3,\"cache_read_input_tokens\":2,\"output_tokens\":1}}}\n\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"not JSON\"}}\n\n" +
        "data: {\"type\":\"ping\"}\n\n" +
        "data: {\"type\":\"future_event\"}\n\n" +
        "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"{\\\"ok\\\":\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"true}\"}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":20}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private sealed class ResponseHandler(string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }
}
