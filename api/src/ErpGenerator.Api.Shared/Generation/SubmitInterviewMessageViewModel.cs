namespace ErpGenerator.Api.Shared.Generation;

public sealed record SubmitInterviewMessageViewModel(
    bool Ok,
    Guid SessionId,
    InterviewMessageViewModel Message,
    PublicInterviewTurnViewModel Turn);
