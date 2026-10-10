public sealed record DeploySampleRequest(
    string Host,
    string? OwnerEmail,
    string? Version = null,
    string? Environment = null,
    bool ApplySeed = false,
    bool RunTests = false,
    string? SeedKey = null);

public sealed record ActivateOwnerRequest(string Token, string Email, string DisplayName, string Password);

public sealed record RuntimeLoginRequest(string Email, string Password);

public sealed record WorkflowDecisionRequest(string? Reason);

public sealed record RuntimeUserInviteRequest(
    string Email,
    string Name,
    IReadOnlyList<string>? Roles,
    long? EmployeeRecordId);

public sealed record RuntimeUserUpdateRequest(
    string? Name,
    IReadOnlyList<string>? Roles);

public sealed record RuntimeUserRoleRequest(string Role);

public sealed record RuntimeUserLinkEmployeeRequest(long EmployeeRecordId);
