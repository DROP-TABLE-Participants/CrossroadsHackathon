namespace ErpGenerator.Api.Shared.Generation;

public sealed record CreateGenerationProjectInputModel(
    string? CompanyName,
    string? Goal,
    string? DiscoveryMode,
    string? TenantSlug = null,
    string? Industry = null,
    string? CompanyType = null,
    string? OwnerName = null,
    string? OwnerEmail = null,
    string? OwnerRoleTitle = null,
    IReadOnlyList<KnownLeadershipContactInputModel>? KnownLeadershipContacts = null,
    string? Description = null,
    IReadOnlyList<InitialInterviewAnswer>? InterviewAnswers = null);

public sealed record InitialInterviewAnswer(string Question, string Answer);
