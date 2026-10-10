using System.Net;
using System.Text;
using System.Text.Json;
using ErpGenerator.Runtime.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeClaudeClientTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"properties":{"highlights":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":1}},"required":["highlights"]}
        """).RootElement.Clone();

    [Fact]
    public async Task Uses_native_Messages_and_reads_text_without_thinking_blocks()
    {
        var handler = new ResponseHandler(Reply("end_turn", """{"highlights":["Available: 200 bars"]}"""));
        var result = await Generate(handler);
        Assert.Equal("Available: 200 bars", JsonDocument.Parse(result).RootElement.GetProperty("highlights")[0].GetString());
        Assert.NotNull(handler.Payload);
        using var payload = JsonDocument.Parse(handler.Payload);
        var root = payload.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.StartsWith("Grounded system prompt", root.GetProperty("system").GetString());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("How many bars?", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(3000, root.GetProperty("max_tokens").GetInt32());
        var format = root.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var highlights = format.GetProperty("schema").GetProperty("properties").GetProperty("highlights");
        Assert.False(highlights.TryGetProperty("maxItems", out _));
        Assert.Equal(1, highlights.GetProperty("minItems").GetInt32());
        Assert.Contains("at most 1", highlights.GetProperty("description").GetString());
        Assert.False(root.TryGetProperty("input", out _));
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("store", out _));
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("refusal")]
    [InlineData("tool_use")]
    [InlineData(null)]
    public async Task Rejects_incomplete_or_refused_responses(string? stopReason)
    {
        var handler = new ResponseHandler(Reply(stopReason, """{"highlights":["One"]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(handler));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"highlights\":[\"One\",\"Two\"]}")]
    [InlineData("{\"highlights\":[]}")]
    public async Task Rejects_malformed_output_and_original_array_bound_violations(string text)
    {
        await Assert.ThrowsAnyAsync<JsonException>(() => Generate(new ResponseHandler(Reply("end_turn", text))));
    }

    [Fact]
    public async Task Provider_failure_preserves_HTTP_status()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Generate(
            new ResponseHandler("{}", HttpStatusCode.TooManyRequests)));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    private static Task<string> Generate(ResponseHandler handler) => new RuntimeClaudeClient(
        new HttpClient(handler), Options.Create(new RuntimeAssistantOptions
        {
            Provider = "Claude", Model = "claude-opus-5-5", ApiKey = "test-key",
            Endpoint = new Uri("https://api.example.test/v1/messages")
        }), NullLogger<RuntimeClaudeClient>.Instance).GenerateStructuredAsync(
            "Grounded system prompt", "How many bars?", "erp_answer", Schema, 3000, CancellationToken.None);

    private static string Reply(string? reason, string text) => JsonSerializer.Serialize(new
    {
        stop_reason = reason,
        content = new object[]
        {
            new { type = "thinking", thinking = "Private reasoning", signature = "signature" },
            new { type = "text", text }
        }
    });

    private sealed class ResponseHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
