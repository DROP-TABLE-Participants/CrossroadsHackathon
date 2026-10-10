using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class ElevenLabsInterviewVoiceInputService(
    HttpClient httpClient,
    IOptions<InterviewVoiceInputOptions> options) : IInterviewVoiceInputService
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ApiKey) &&
        !options.Value.ApiKey.TrimStart().StartsWith('<');

    public async Task<InterviewVoiceTokenViewModel> MintTokenAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!IsConfigured) throw new InvalidOperationException("Voice input is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint);
        request.Headers.TryAddWithoutValidation("xi-api-key", settings.ApiKey);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Voice token provider returned status {(int)response.StatusCode}.");
        }
        var body = await response.Content.ReadFromJsonAsync<VoiceTokenProviderResponse>(cancellationToken)
            ?? throw new HttpRequestException("Voice token provider returned an empty response.");
        if (string.IsNullOrWhiteSpace(body.Token))
        {
            throw new HttpRequestException("Voice token provider response did not contain a token.");
        }
        return new InterviewVoiceTokenViewModel(body.Token, settings.RealtimeHost, settings.ModelId);
    }
}
