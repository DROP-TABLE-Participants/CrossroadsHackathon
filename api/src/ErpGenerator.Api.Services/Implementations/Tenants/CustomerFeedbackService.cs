using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class CustomerFeedbackService(ErpGeneratorDbContext dbContext) : ICustomerFeedbackService
{
    public async Task<ErpFeedbackListViewModel> ListFeedbackAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.RuntimeFeedback.AsNoTracking()
            .Include(value => value.TenantEnvironment).ThenInclude(value => value.Tenant)
            .Where(value => value.TenantEnvironment.Tenant.OwnerId == customerId)
            .OrderByDescending(value => value.CreatedAt).Take(100).ToListAsync(cancellationToken);
        return new ErpFeedbackListViewModel(rows.Select(value => new ErpFeedbackViewModel(
            value.Id, value.TenantEnvironment.TenantId, value.TenantEnvironmentId,
            value.TenantEnvironment.Environment, value.RuntimeUserEmail, value.EntityKey,
            value.RecordId, value.PagePath, value.Category, value.Message, "received", value.CreatedAt)).ToArray());
    }

    public async Task<ErpUsageObservationListViewModel> ListUsageAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.RuntimeUsageEvents.AsNoTracking()
            .Include(value => value.TenantEnvironment).ThenInclude(value => value.Tenant)
            .Where(value => value.TenantEnvironment.Tenant.OwnerId == customerId)
            .OrderByDescending(value => value.OccurredAt).Take(100).ToListAsync(cancellationToken);
        return new ErpUsageObservationListViewModel(rows.Select(Map).ToArray());
    }

    public async Task<ErpUsageObservationViewModel> DismissUsageAsync(
        Guid customerId,
        Guid observationId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.RuntimeUsageEvents
            .Include(value => value.TenantEnvironment).ThenInclude(value => value.Tenant)
            .SingleOrDefaultAsync(value => value.Id == observationId && value.TenantEnvironment.Tenant.OwnerId == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Usage observation was not found.");
        row.DismissedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    private static ErpUsageObservationViewModel Map(RuntimeUsageEvent value)
    {
        var subject = value.EntityKey ?? value.WorkflowKey ?? value.ReportKey;
        return new ErpUsageObservationViewModel(
            value.Id, value.TenantEnvironment.TenantId, value.TenantEnvironmentId,
            value.TenantEnvironment.Environment, value.DismissedAt is null ? "observed" : "dismissed",
            value.EventKind, "info", Humanize(value.EventKind),
            subject is null ? "Runtime activity was observed." : $"Runtime activity was observed for {subject}.",
            1, value.EntityKey, value.ReportKey, value.WorkflowKey, value.OccurredAt);
    }

    private static string Humanize(string value)
    {
        var text = value.Replace('_', ' ').Replace('.', ' ').Trim();
        return text.Length == 0 ? "Usage observed" : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
