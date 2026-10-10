namespace ErpGenerator.Api.Shared.Generation;

public sealed record PublicInterviewRequestViewModel(string Status, string QuestionSetKey, DateTimeOffset? ExpiresAt);
