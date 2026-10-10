namespace ErpGenerator.Api.Shared.Generation;

public sealed record PublicInterviewViewModel(
    PublicInterviewProjectViewModel Project,
    PublicInterviewParticipantViewModel Participant,
    PublicInterviewRequestViewModel Request,
    PublicInterviewPendingWorkViewModel? PendingWork,
    PublicInterviewCompletionViewModel Completion,
    PublicInterviewTurnViewModel? Turn,
    IReadOnlyList<InterviewMessageViewModel> Messages);
