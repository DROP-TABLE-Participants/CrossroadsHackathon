namespace ErpGenerator.Api.Shared.Admin;

public sealed record CreateAdminAdministratorInputModel(
    string? Email,
    string? Name,
    string? Locale);
