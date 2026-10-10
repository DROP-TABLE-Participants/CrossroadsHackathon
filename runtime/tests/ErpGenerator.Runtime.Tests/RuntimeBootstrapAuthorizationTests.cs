using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeBootstrapAuthorizationTests
{
    [Fact]
    public void Tenant_bootstrap_owner_authority_is_separate_from_bundle_role_grants()
    {
        var permissions = new RuntimePermissionService(null!);
        var bootstrapOwner = new RuntimePermissionSnapshot(true, []);
        var ordinaryOwnerWithoutGrants = RuntimePermissionSnapshot.Empty;

        permissions.IsAllowed(bootstrapOwner, "admin", entity: null).Should().BeTrue();
        permissions.IsAllowed(ordinaryOwnerWithoutGrants, "admin", entity: null).Should().BeFalse();
        RuntimeSystemRoleIds.TenantOwner.Should().Be("_runtime_owner");
    }

    [Fact]
    public void Ordinary_generated_owner_is_enforced_by_its_explicit_grants()
    {
        var permissions = new RuntimePermissionService(null!);
        var customer = new RuntimeEntityDefinition(
            "customers",
            "customer",
            "customers",
            "Customers",
            "app",
            "customers",
            "company_name",
            null,
            []);
        var generatedOwner = new RuntimePermissionSnapshot(
            false,
            [new RuntimePermissionGrant("read", "customer")]);

        permissions.IsAllowed(generatedOwner, "read", customer).Should().BeTrue();
        permissions.IsAllowed(generatedOwner, "create", customer).Should().BeFalse();
        permissions.IsAllowed(generatedOwner, "admin", entity: null).Should().BeFalse();
    }

    [Fact]
    public void Archived_and_global_runtime_views_do_not_treat_generated_owner_as_system_owner()
    {
        var permissions = new RuntimePermissionService(null!);
        var entity = CustomerEntity();
        var generatedOwner = new RuntimePermissionSnapshot(
            false,
            [new RuntimePermissionGrant("read", "customer")],
            ["owner"]);
        var systemOwner = new RuntimePermissionSnapshot(
            true,
            [],
            [RuntimeSystemRoleIds.TenantOwner]);

        permissions.IsAllowed(generatedOwner, "restore", entity).Should().BeFalse(
            "including archived records requires an explicit restore grant");
        permissions.IsAllowed(generatedOwner, "admin", entity: null).Should().BeFalse(
            "the generated role name 'owner' must not unlock global runtime views");
        permissions.IsAllowed(systemOwner, "restore", entity).Should().BeTrue();
        permissions.IsAllowed(systemOwner, "admin", entity: null).Should().BeTrue();
    }

    [Fact]
    public void Explicit_task_visibility_does_not_give_generated_owner_global_notification_authority()
    {
        var permissions = new RuntimePermissionService(null!);
        var entity = CustomerEntity();
        var generatedOwner = new RuntimePermissionSnapshot(
            false,
            [
                new RuntimePermissionGrant("read", "customer"),
                new RuntimePermissionGrant("read_task", "customer")
            ],
            ["owner"]);
        var systemOwner = new RuntimePermissionSnapshot(
            true,
            [],
            [RuntimeSystemRoleIds.TenantOwner]);

        permissions.IsAllowed(generatedOwner, "read", entity).Should().BeTrue();
        permissions.IsAllowed(generatedOwner, "read_task", entity).Should().BeTrue();
        permissions.IsAllowed(generatedOwner, "admin", entity: null).Should().BeFalse();
        generatedOwner.EffectiveRoleIds.Should().Equal("owner");

        permissions.IsAllowed(systemOwner, "read_task", entity).Should().BeTrue();
        permissions.IsAllowed(systemOwner, "admin", entity: null).Should().BeTrue();
        systemOwner.EffectiveRoleIds.Should().Equal(RuntimeSystemRoleIds.TenantOwner);
    }

    private static RuntimeEntityDefinition CustomerEntity() => new(
        "customers",
        "customer",
        "customers",
        "Customers",
        "app",
        "customers",
        "company_name",
        null,
        []);
}
