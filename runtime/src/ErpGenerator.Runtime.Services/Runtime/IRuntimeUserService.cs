using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeUserService
{
    Task<RuntimeUserContext?> GetActiveUserContextAsync(ResolvedTenantEnvironment tenant, long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeManagedUser>> ListUsersAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeRoleOption>> ListRolesAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default);
    Task<RuntimeUserInviteResult> InviteUserAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, string email, string displayName, IReadOnlyList<string>? roles, long? employeeRecordId, TimeSpan lifetime, CancellationToken cancellationToken = default);
    Task<bool> ActivateInvitedUserAsync(ResolvedTenantEnvironment tenant, string token, string email, string displayName, string password, CancellationToken cancellationToken = default);
    Task<bool> UpdateUserAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, string? displayName, IReadOnlyList<string>? roles, CancellationToken cancellationToken = default);
    Task<bool> AddRoleAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, string role, CancellationToken cancellationToken = default);
    Task<bool> RemoveRoleAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, string role, CancellationToken cancellationToken = default);
    Task<bool> SetUserEnabledAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> LinkEmployeeAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, long employeeRecordId, CancellationToken cancellationToken = default);
    Task<RuntimeUserRecordLink> BindUserToRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, string entityKey, long recordId, CancellationToken cancellationToken = default);
    Task<bool> UnlinkUserFromRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, string entityKey, long recordId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeUserRecordLink>> ListUserRecordLinksAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, CancellationToken cancellationToken = default);
    Task<bool> UnlinkEmployeeAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext actor, long userId, CancellationToken cancellationToken = default);
}
