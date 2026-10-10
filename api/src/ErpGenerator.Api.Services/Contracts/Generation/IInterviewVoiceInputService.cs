namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IInterviewVoiceInputService
{
    bool IsConfigured { get; }
    Task<InterviewVoiceTokenViewModel> MintTokenAsync(CancellationToken cancellationToken);
}
