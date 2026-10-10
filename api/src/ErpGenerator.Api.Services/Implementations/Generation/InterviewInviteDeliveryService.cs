namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class InterviewInviteDeliveryService(IEmailDeliveryService emailDelivery)
    : IInterviewInviteDeliveryService
{
    public async Task<string> DeliverAsync(
        InterviewInviteDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RecipientEmail)) return "skipped_no_email";
        if (!emailDelivery.IsEnabled) return "not_sent";
        await emailDelivery.SendAsync(
            request.RecipientEmail,
            $"{request.CompanyName} invited you to a ErpGenerator interview",
            $"You have been invited to answer a short ERP-discovery question for {request.CompanyName}.\n\nOpen the secure interview link:\n{request.InviteLink}\n\nThis link expires in 14 days.",
            cancellationToken);
        return "sent";
    }
}
