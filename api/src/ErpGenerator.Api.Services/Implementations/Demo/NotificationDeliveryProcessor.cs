using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Demo;

public sealed class NotificationDeliveryProcessor(
    ErpGeneratorDbContext dbContext,
    IEmailDeliveryService emailDelivery) : INotificationDeliveryProcessor
{
    public async Task<bool> TryProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var delivery = await dbContext.NotificationDeliveries
            .Where(value => (value.Status == "pending" || value.Status == "retrying") &&
                            (!value.NextAttemptAt.HasValue || value.NextAttemptAt <= now))
            .OrderBy(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (delivery is null) return false;

        delivery.Status = "processing";
        delivery.AttemptCount += 1;
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            await emailDelivery.SendAsync(delivery.Recipient, delivery.Subject, delivery.Body, cancellationToken);
            delivery.Status = "sent";
            delivery.SentAt = DateTimeOffset.UtcNow;
            delivery.NextAttemptAt = null;
            delivery.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            delivery.Status = "retrying";
            delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            delivery.Status = delivery.AttemptCount >= 5 ? "failed" : "retrying";
            delivery.NextAttemptAt = delivery.Status == "retrying"
                ? DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, delivery.AttemptCount - 1))
                : null;
            delivery.LastError = SafeError(exception);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string SafeError(Exception exception) => exception switch
    {
        InvalidOperationException => "Email delivery is not configured for this environment.",
        _ => "Email delivery failed. Inspect the API service logs for the provider error."
    };
}
