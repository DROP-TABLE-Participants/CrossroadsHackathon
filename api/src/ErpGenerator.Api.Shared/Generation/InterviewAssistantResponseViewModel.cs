namespace ErpGenerator.Api.Shared.Generation;

public sealed record InterviewAssistantResponseViewModel(
    string Decision,
    bool Completed,
    InterviewMessageViewModel Message,
    PublicInterviewTurnViewModel Turn,
    bool Retryable = false,
    int? PollAfterMs = null);
