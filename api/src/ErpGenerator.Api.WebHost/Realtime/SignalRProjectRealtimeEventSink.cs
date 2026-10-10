using Microsoft.AspNetCore.SignalR;
using ErpGenerator.Api.Services.Contracts.Realtime;

namespace ErpGenerator.Api.WebHost.Realtime;

public sealed class SignalRProjectRealtimeEventSink(
    IHubContext<CustomerRealtimeHub> customerHubContext) : IProjectRealtimeEventSink
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
    }
}
