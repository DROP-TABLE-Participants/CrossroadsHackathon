namespace ErpGenerator.Runtime.Services.Auth;

public interface IRuntimeSessionService
{
    string CreateSession(string tenantStableId, string host, long userId, string email, string role);
    RuntimeSession? ValidateSession(string? token, string tenantStableId, string host);
}
