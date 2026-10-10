using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using ErpGenerator.Api.Services.Contracts.Realtime;
using ErpGenerator.Api.WebHost.Realtime;

namespace ErpGenerator.Api.Tests;

public sealed class SignalRProjectRealtimeEventSinkTests
{
    [Fact]
    public async Task One_canonical_change_notifies_customer_and_administrator_clients()
    {
        var customer = new RecordingHubContext<CustomerRealtimeHub>();
        var administrator = new RecordingHubContext<AdminRealtimeHub>();
        var sink = new SignalRProjectRealtimeEventSink(
            customer,
            administrator);
        var projectId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await sink.SendAsync(
            new ProjectRealtimeEvent(
                "projectChanged",
                customerId,
                projectId,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var customerMessage = Assert.Single(customer.Proxy.Messages);
        Assert.Equal("projectChanged", customerMessage.Method);
        Assert.Contains(
            projectId.ToString(),
            JsonSerializer.Serialize(customerMessage.Arguments));
        Assert.Equal(
            $"customer:{customerId:D}",
            Assert.Single(customer.Clients.RequestedGroups));

        var adminMessage = Assert.Single(administrator.Proxy.Messages);
        Assert.Equal("orchestrationChanged", adminMessage.Method);
        Assert.Contains(
            projectId.ToString(),
            JsonSerializer.Serialize(adminMessage.Arguments));
    }

    private sealed class RecordingHubContext<THub> : IHubContext<THub>
        where THub : Hub
    {
        public RecordingClientProxy Proxy { get; } = new();
        public RecordingHubClients Clients { get; }
        IHubClients IHubContext<THub>.Clients => Clients;
        public IGroupManager Groups { get; } = new NoopGroupManager();

        public RecordingHubContext()
        {
            Clients = new RecordingHubClients(Proxy);
        }
    }

    private sealed class RecordingHubClients(RecordingClientProxy proxy)
        : IHubClients
    {
        public List<string> RequestedGroups { get; } = [];
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;

        public IClientProxy Group(string groupName)
        {
            RequestedGroups.Add(groupName);
            return proxy;
        }

        public IClientProxy GroupExcept(
            string groupName,
            IReadOnlyList<string> excludedConnectionIds)
        {
            RequestedGroups.Add(groupName);
            return proxy;
        }

        public IClientProxy Groups(IReadOnlyList<string> groupNames)
        {
            RequestedGroups.AddRange(groupNames);
            return proxy;
        }

        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public List<RecordedMessage> Messages { get; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(new RecordedMessage(method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed record RecordedMessage(
        string Method,
        IReadOnlyList<object?> Arguments);
}
