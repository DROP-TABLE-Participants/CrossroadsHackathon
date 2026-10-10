namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IInterviewInviteDeliveryService
{
    Task<string> DeliverAsync(InterviewInviteDeliveryRequest request, CancellationToken cancellationToken);
}
