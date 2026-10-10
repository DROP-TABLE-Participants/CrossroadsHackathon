namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminNotificationDeliveryViewModel(
    Guid Id,
    string Kind,
    string Recipient,
    string Status,
    int AttemptCount,
    DateTimeOffset? SentAt,
    DateTimeOffset? NextAttemptAt,
    string? LastError);
