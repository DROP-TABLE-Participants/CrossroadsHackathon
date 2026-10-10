using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;
using ErpGenerator.Api.Data.Entities.Identity;

namespace ErpGenerator.Api.Data.Entities.Demo;

public sealed class DemoBooking : ControlPlaneEntity
{
    public Guid CustomerId { get; set; }
    public CustomerAccount Customer { get; set; } = null!;
    [MaxLength(320)] public string Email { get; set; } = string.Empty;
    [MaxLength(50)] public string PhoneNumber { get; set; } = string.Empty;
    [MaxLength(120)] public string RepresentativeName { get; set; } = string.Empty;
    [MaxLength(120)] public string? Position { get; set; }
    [MaxLength(160)] public string CompanyName { get; set; } = string.Empty;
    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset EndDate { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "reserved";
    public ICollection<NotificationDelivery> NotificationDeliveries { get; set; } = [];
}
