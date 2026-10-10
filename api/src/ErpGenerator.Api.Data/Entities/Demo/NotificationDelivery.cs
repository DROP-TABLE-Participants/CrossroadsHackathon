using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Demo;

public sealed class NotificationDelivery : ControlPlaneEntity
{
    public Guid? DemoBookingId { get; set; }
    public DemoBooking? DemoBooking { get; set; }
    [MaxLength(80)] public string Kind { get; set; } = string.Empty;
    [MaxLength(320)] public string Recipient { get; set; } = string.Empty;
    [MaxLength(240)] public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
}
