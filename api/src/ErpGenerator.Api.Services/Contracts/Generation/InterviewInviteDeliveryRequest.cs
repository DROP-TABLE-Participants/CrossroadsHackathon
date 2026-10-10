namespace ErpGenerator.Api.Services.Contracts.Generation;

public sealed record InterviewInviteDeliveryRequest(
    string? RecipientEmail,
    string CompanyName,
    string InviteLink);
