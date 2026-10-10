namespace ErpGenerator.Api.Shared.Generation;

public sealed record GenerationProjectParticipantViewModel(
    Guid Id,
    string Name,
    string? Email,
    string? RoleTitle,
    string? Department,
    string Status);
