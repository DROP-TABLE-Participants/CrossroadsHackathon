namespace ErpGenerator.Runtime.Services.Auth;

public sealed record RuntimeSession(
    string TenantStableId,
    string Host,
    long UserId,
    string Email,
    string Role,
    DateTimeOffset ExpiresAt);
