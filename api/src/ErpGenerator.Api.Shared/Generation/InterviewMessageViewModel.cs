namespace ErpGenerator.Api.Shared.Generation;

public sealed record InterviewMessageViewModel(
    Guid Id,
    string Role,
    string Content,
    string MessageKind,
    string? QuestionKey,
    DateTimeOffset CreatedAt);
