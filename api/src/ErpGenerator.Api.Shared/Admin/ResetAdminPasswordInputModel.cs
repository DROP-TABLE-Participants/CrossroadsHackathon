namespace ErpGenerator.Api.Shared.Admin;

public sealed record ResetAdminPasswordInputModel(string? Email, string? Token, string? NewPassword);
