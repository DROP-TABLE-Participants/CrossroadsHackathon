using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Realtime;

[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
public sealed class CustomerRealtimeHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var customerId = Context.User?.RequireCustomerId()
            ?? throw new HubException("Customer authentication is required.");
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroups.Customer(customerId),
            Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
