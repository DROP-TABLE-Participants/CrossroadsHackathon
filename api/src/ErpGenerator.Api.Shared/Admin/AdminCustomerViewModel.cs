namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminCustomerViewModel(
    Guid Id,
    string Email,
    string Name,
    string? CompanyName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int GenerationProjectCount);
