namespace ErpGenerator.Api.Services.Contracts.Realtime;

public interface IProjectRealtimeEventSink
{
    Task SendAsync(ProjectRealtimeEvent message, CancellationToken cancellationToken);
}
