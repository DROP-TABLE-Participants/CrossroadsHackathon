namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminViewModel(
    Guid Id,
    string Email,
    string Name,
    string Role);
