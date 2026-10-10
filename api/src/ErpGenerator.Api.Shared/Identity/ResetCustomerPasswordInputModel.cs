namespace ErpGenerator.Api.Shared.Identity;

public sealed record ResetCustomerPasswordInputModel(string? Email, string? Token, string? NewPassword);
