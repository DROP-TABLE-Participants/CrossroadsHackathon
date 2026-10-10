namespace ErpGenerator.Api.Services.Contracts.Realtime;

public sealed record ProjectRealtimeEvent(
    string EventName,
    Guid CustomerId,
    Guid ProjectId,
    DateTimeOffset ChangedAt);
