namespace ErpGenerator.Api.Services.Implementations.Admin;

internal static class AdminViewModelMapper
{
    public static AdminAuditRecordViewModel Audit(AdminAuditRecord value) => new(
        value.Id,
        value.ActorAdminAccountId,
        value.ActorEmailSnapshot,
        value.Action,
        value.ResourceType,
        value.ResourceId,
        value.SafeDetailsJson,
        value.CreatedAt);

    public static AdminNotificationDeliveryViewModel Notification(NotificationDelivery value) => new(
        value.Id,
        value.Kind,
        value.Recipient,
        value.Status,
        value.AttemptCount,
        value.SentAt,
        value.NextAttemptAt,
        value.LastError);
}
