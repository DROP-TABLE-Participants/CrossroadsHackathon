namespace ErpGenerator.Api.Shared.Generation;

public sealed record PublicInterviewTurnViewModel(string TurnKey, string Status, DateTimeOffset? AvailableAt = null);
