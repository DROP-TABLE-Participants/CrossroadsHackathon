namespace ErpGenerator.Api.Services.Options.Generation;

public sealed class InterviewVoiceInputOptions
{
    public const string SectionName = "InterviewVoice";
    public string ApiKey { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = "https://api.elevenlabs.io/v1/single-use-token/realtime_scribe";
    public string RealtimeHost { get; set; } = "api.elevenlabs.io";
    public string ModelId { get; set; } = "scribe_v2_realtime";
}
