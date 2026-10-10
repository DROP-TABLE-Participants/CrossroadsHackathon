namespace ErpGenerator.Api.Shared.Generation;

public sealed record InterviewRequestViewModel(
    Guid Id,
    Guid? ParticipantId,
    string Status,
    string QuestionSetKey,
    DateTimeOffset? ExpiresAt,
    string? InviteLink = null,
    DateTimeOffset? CompletedAt = null,
    InterviewInviteDeliveryViewModel? InviteDelivery = null);
