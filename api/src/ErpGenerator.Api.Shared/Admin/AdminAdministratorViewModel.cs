namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminAdministratorViewModel(
    Guid Id,
    string Email,
    string Name,
    bool IsActive,
    string Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
