using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ErpGenerator.Api.Data.Context;
using ErpGenerator.Api.Data.Realtime;
using ErpGenerator.Api.Services.Contracts.Realtime;

namespace ErpGenerator.Api.Services.Implementations.Realtime;

public sealed class PostgresControlPlaneRealtimePublisher(
    ILogger<PostgresControlPlaneRealtimePublisher> logger) : IControlPlaneRealtimePublisher
{
    internal const string Channel = "erpgenerator_project_events";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(
        ErpGeneratorDbContext dbContext,
        IReadOnlyCollection<ControlPlaneProjectChange> changes,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return;
        }

        var unresolvedIds = changes
            .Where(value => value.CustomerId is null && !value.Removed)
            .Select(value => value.ProjectId)
            .Distinct()
            .ToArray();
        var owners = unresolvedIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await dbContext.GenerationProjects
                .AsNoTracking()
                .Where(value => unresolvedIds.Contains(value.Id))
                .Select(value => new { value.Id, value.CustomerId })
                .ToDictionaryAsync(value => value.Id, value => value.CustomerId, cancellationToken);

        foreach (var change in changes)
        {
            var customerId = change.CustomerId ??
                (owners.TryGetValue(change.ProjectId, out var ownerId) ? ownerId : (Guid?)null);
            if (customerId is null)
            {
                logger.LogWarning(
                    "Skipped realtime project event for {ProjectId} because its customer could not be resolved.",
                    change.ProjectId);
                continue;
            }

            var payload = JsonSerializer.Serialize(
                new ProjectRealtimeEvent(
                    change.Removed ? "projectRemoved" : "projectChanged",
                    customerId.Value,
                    change.ProjectId,
                    DateTimeOffset.UtcNow),
                JsonOptions);
            try
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_notify({Channel}, {payload})",
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Could not publish realtime project event for {ProjectId}.",
                    change.ProjectId);
            }
        }
    }
}
