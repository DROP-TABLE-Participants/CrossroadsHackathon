namespace ErpGenerator.Api.Services.Contracts.Identity;

public sealed record AccessTokenSubject(Guid Id, string? Email, string SecurityStamp);
