namespace ErpGenerator.Api.Shared.Generation;

public sealed record InterviewInviteDeliveryViewModel(
    string Status,
    string? RecipientEmail,
    DateTimeOffset? SentAt,
    DateTimeOffset? LastAttemptAt,
    string? FailureReasonSafe,
    string? Provider,
    int AttemptCount);
