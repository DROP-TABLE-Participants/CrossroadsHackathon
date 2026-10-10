namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IGenerationWorkflowService
{
    Task<PendingWorkResolutionViewModel> AddEvidenceAsync(Guid customerId, Guid itemId, AddEvidenceDocumentInputModel input, CancellationToken cancellationToken);
    Task<PendingWorkResolutionViewModel> ResolveAsync(Guid customerId, Guid itemId, ResolvePendingWorkInputModel input, CancellationToken cancellationToken);
    Task<InterviewInviteViewModel> CreateInterviewAsync(Guid customerId, Guid itemId, CreateInterviewRequestInputModel input, CancellationToken cancellationToken);
    Task<InterviewInviteViewModel> RotateInviteAsync(Guid customerId, Guid requestId, CancellationToken cancellationToken);
    Task<InterviewInviteViewModel> SendInviteAsync(Guid customerId, Guid requestId, CancellationToken cancellationToken);
    Task<PublicInterviewEnvelope?> GetInterviewAsync(string token, CancellationToken cancellationToken);
    Task<PublicInterviewEnvelope> StartInterviewAsync(string token, CancellationToken cancellationToken);
    Task<SubmitInterviewMessageViewModel> AddMessageAsync(string token, SubmitInterviewMessageInputModel input, CancellationToken cancellationToken);
    Task<InterviewAssistantResponseViewModel> CompleteTurnAsync(string token, RequestInterviewAssistantInputModel input, CancellationToken cancellationToken);
    Task<PublicInterviewEnvelope> AddAnswerAsync(string token, SubmitInterviewAnswerInputModel input, CancellationToken cancellationToken);
    Task<PublicInterviewEnvelope> CompleteInterviewAsync(string token, CompleteInterviewInputModel input, CancellationToken cancellationToken);
}
