using FluentAssertions;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeUserTestHelper
{
    public static RuntimeUserContext Owner(long userId = 1, string email = "owner@acme.test") => new(userId, email, "owner")
    {
        Roles = ["owner"]
    };

    public static async Task<RuntimeUserContext> ActivateOwnerAsync(
        RuntimeDatabase runtimeDatabase,
        ResolvedTenantEnvironment tenant,
        string email,
        string name = "Owner",
        string password = "OwnerPass123!")
    {
        var token = await runtimeDatabase.CreateActivationTokenAsync(tenant, email, TimeSpan.FromDays(7));
        (await runtimeDatabase.ActivateOwnerAsync(tenant, token, email, name, password)).Should().BeTrue();
        var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, password);
        login.Should().NotBeNull();
        return new RuntimeUserContext(login!.Id, login.Email, login.Role) { Roles = login.Roles };
    }

    public static async Task<RuntimeUserContext> InviteAndActivateMemberAsync(
        RuntimeUserStore users,
        RuntimeDatabase runtimeDatabase,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext owner,
        string email,
        long? linkedEmployeeId = null,
        string name = "Member",
        string password = "MemberPass123!")
    {
        var invite = await users.InviteUserAsync(tenant, owner, email, name, ["member"], linkedEmployeeId, TimeSpan.FromDays(7));
        invite.ActivationToken.Should().NotBeNullOrWhiteSpace();
        (await users.ActivateInvitedUserAsync(tenant, invite.ActivationToken!, email, name, password)).Should().BeTrue();
        var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, password);
        login.Should().NotBeNull();
        return new RuntimeUserContext(login!.Id, login.Email, login.Role) { Roles = login.Roles };
    }
}
