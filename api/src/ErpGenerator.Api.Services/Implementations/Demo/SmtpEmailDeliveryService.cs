using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Demo;

public sealed class SmtpEmailDeliveryService(IOptions<InterviewEmailOptions> options) : IEmailDeliveryService
{
    public bool IsEnabled => string.Equals(options.Value.Mode, "Smtp", StringComparison.OrdinalIgnoreCase);

    public async Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled) throw new InvalidOperationException("Email delivery is disabled.");
        var settings = options.Value;
        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(recipient));
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.Username, settings.Password)
        };
        using var registration = cancellationToken.Register(client.SendAsyncCancel);
        await client.SendMailAsync(message, cancellationToken);
    }
}
