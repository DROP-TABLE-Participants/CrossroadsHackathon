using Microsoft.AspNetCore.SignalR;
using ErpGenerator.Api.Services.Contracts.Realtime;

namespace ErpGenerator.Api.WebHost.Realtime;

public sealed class SignalRProjectRealtimeEventSink(
    IHubContext<CustomerRealtimeHub> customerHubContext,
    IHubContext<AdminRealtimeHub> adminHubContext) : IProjectRealtimeEventSink
{
    public async Task SendAsync(
        ProjectRealtimeEvent message,
        CancellationToken cancellationToken)
    {
        await customerHubContext.Clients
            .Group(RealtimeGroups.Customer(message.CustomerId))
            .SendAsync(
                message.EventName,
                new { projectId = message.ProjectId, changedAt = message.ChangedAt },
                cancellationToken);
        await adminHubContext.Clients.All.SendAsync(
            "orchestrationChanged",
            new
            {
                projectId = message.ProjectId,
                eventName = message.EventName,
                changedAt = message.ChangedAt
            },
            cancellationToken);
    }
}
