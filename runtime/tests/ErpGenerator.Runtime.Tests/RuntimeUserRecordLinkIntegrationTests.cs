using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeUserRecordLinkIntegrationTests
{
    [Fact]
    public void Runtime_user_link_schema_allows_shared_business_records_but_keeps_employees_exclusive()
    {
        var statements = new RuntimeSqlBuilder().BuildDeploymentStatements(
            new ErpBundleCompiler().Compile(Bundle()));

        statements.Should().Contain(statement =>
            statement.Contains(
                "drop constraint if exists runtime_user_links_entity_key_record_id_key",
                StringComparison.OrdinalIgnoreCase));
        statements.Should().Contain(statement =>
            statement.Contains("ux_runtime_user_links_employee_record", StringComparison.OrdinalIgnoreCase) &&
            statement.Contains("where lower(entity_key) in ('employee', 'employees')", StringComparison.OrdinalIgnoreCase));
        statements.Should().NotContain(statement =>
            statement.Contains("unique (entity_key, record_id)", StringComparison.OrdinalIgnoreCase));
    }

    [PostgresFact]
    public async Task Generic_links_are_canonical_idempotent_shareable_and_exactly_cleaned_up()
    {
        await WithRuntimeAsync(async fixture =>
        {
            var warehouseOne = await fixture.CreateRecordAsync("warehouses", "North warehouse");
            var warehouseTwo = await fixture.CreateRecordAsync("warehouse", "South warehouse");
            var department = await fixture.CreateRecordAsync("departments", "Operations");
            var first = await fixture.ActivateUserAsync("member", "first");
            var second = await fixture.ActivateUserAsync("member", "second");

            var firstWarehouse = await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "warehouses",
                warehouseOne);
            var idempotent = await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "warehouse",
                warehouseOne);
            await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                "warehouse",
                warehouseOne);
            await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "departments",
                department);

            firstWarehouse.EntityId.Should().Be("warehouse");
            idempotent.Should().Be(firstWarehouse);
            (await fixture.Users.ListUserRecordLinksAsync(fixture.Tenant, fixture.Owner, first.UserId))
                .Should().BeEquivalentTo(
                [
                    new { EntityId = "warehouse", RecordId = warehouseOne },
                    new { EntityId = "department", RecordId = department }
                ],
                options => options.ExcludingMissingMembers());
            (await fixture.Users.ListUserRecordLinksAsync(fixture.Tenant, fixture.Owner, second.UserId))
                .Should().ContainSingle(link =>
                    link.EntityId == "warehouse" && link.RecordId == warehouseOne);
            (await fixture.Users.ListUsersAsync(fixture.Tenant))
                .Single(user => user.Id == first.UserId)
                .LinkedEmployee.Should().BeNull("generic record links are not employee profile links");

            var rebind = () => fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "warehouse",
                warehouseTwo);
            await rebind.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("*already linked to a different*warehouse*record*");

            (await fixture.Users.UnlinkUserFromRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "warehouse",
                warehouseTwo)).Should().BeFalse();
            (await fixture.Users.UnlinkUserFromRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "warehouses",
                warehouseOne)).Should().BeTrue();

            var remaining = await fixture.Users.ListUserRecordLinksAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId);
            remaining.Should().ContainSingle(link =>
                link.EntityId == "department" && link.RecordId == department);
            await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "warehouse",
                warehouseTwo);
        });
    }

    [PostgresFact]
    public async Task Employee_exclusivity_and_active_user_and_record_invariants_fail_closed()
    {
        await WithRuntimeAsync(async fixture =>
        {
            var employee = await fixture.CreateRecordAsync("employees", "Employee A");
            var warehouse = await fixture.CreateRecordAsync("warehouses", "Archived warehouse");
            var department = await fixture.CreateRecordAsync("departments", "Finance");
            var first = await fixture.ActivateUserAsync("member", "employee-first");
            var second = await fixture.ActivateUserAsync("member", "employee-second");

            var firstLink = await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "employees",
                employee);
            (await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "employee",
                employee)).Should().Be(firstLink);

            var duplicateEmployee = () => fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                "employee",
                employee);
            await duplicateEmployee.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("*already linked to another runtime user*");

            var missingRecord = () => fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                "warehouse",
                9_999_999);
            await missingRecord.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("Active 'warehouse' record*was not found*");

            (await fixture.Records.ArchiveRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                "warehouse",
                warehouse,
                "Test archived link target")).Should().BeTrue();
            var archivedRecord = () => fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                "warehouse",
                warehouse);
            await archivedRecord.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("Active 'warehouse' record*was not found*");

            (await fixture.Users.SetUserEnabledAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                enabled: false)).Should().BeTrue();
            var disabledUser = () => fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                "department",
                department);
            await disabledUser.Should().ThrowAsync<RuntimeUserValidationException>()
                .WithMessage("Active runtime user*was not found*");

            (await fixture.Users.UnlinkUserFromRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                first.UserId,
                "employee",
                employee)).Should().BeTrue();
            (await fixture.Users.SetUserEnabledAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                enabled: true)).Should().BeTrue();
            await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                fixture.Owner,
                second.UserId,
                "employee",
                employee);
        });
    }

    [PostgresFact]
    public async Task Only_durable_active_runtime_administrators_can_manage_links()
    {
        await WithRuntimeAsync(async fixture =>
        {
            var warehouse = await fixture.CreateRecordAsync("warehouse", "Secure warehouse");
            var member = await fixture.ActivateUserAsync("member", "ordinary-member");
            var admin = await fixture.ActivateUserAsync("admin", "runtime-admin");

            var memberBind = () => fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                member,
                member.UserId,
                "warehouse",
                warehouse);
            await memberBind.Should().ThrowAsync<RuntimeUserPermissionException>();

            await fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                admin,
                member.UserId,
                "warehouse",
                warehouse);
            (await fixture.Users.ListUserRecordLinksAsync(fixture.Tenant, admin, member.UserId))
                .Should().ContainSingle();

            (await fixture.Users.SetUserEnabledAsync(
                fixture.Tenant,
                fixture.Owner,
                admin.UserId,
                enabled: false)).Should().BeTrue();
            var staleAdminList = () => fixture.Users.ListUserRecordLinksAsync(
                fixture.Tenant,
                admin,
                member.UserId);
            await staleAdminList.Should().ThrowAsync<RuntimeUserPermissionException>();

            var syntheticOwner = new RuntimeUserContext(
                9_999_999,
                "synthetic-owner@example.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var syntheticBind = () => fixture.Users.BindUserToRecordAsync(
                fixture.Tenant,
                syntheticOwner,
                member.UserId,
                "department",
                warehouse);
            await syntheticBind.Should().ThrowAsync<RuntimeUserPermissionException>();
        });
    }

    private static async Task WithRuntimeAsync(Func<RuntimeUserLinkFixture, Task> assertion)
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_user_links_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var tenant = RuntimeTestTenant.Create(
                "user-links",
                "user-links.erpgenerator.example",
                databaseName,
                TenantEnvironmentKind.Erp);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyCompiledBundleAsync(
                tenant,
                new ErpBundleCompiler().Compile(Bundle()));
            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"owner-{suffix}@example.test");
            await assertion(new RuntimeUserLinkFixture(
                tenant,
                runtimeDatabase,
                new RuntimeRecordStore(factory),
                new RuntimeUserStore(factory),
                owner,
                suffix));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static ErpBundle Bundle() => new()
    {
        Id = "runtime_user_links",
        Name = "Runtime user link qualification",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "organization",
                Name = "Organization",
                Entities =
                [
                    Entity("employee", "employees"),
                    Entity("warehouse", "warehouses"),
                    Entity("department", "departments")
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["admin"] = new()
            {
                DisplayName = "Administrator",
                Grants = [new ErpGrant { Action = "admin", Entity = "*" }]
            },
            ["member"] = new()
            {
                DisplayName = "Member",
                Grants = [new ErpGrant { Action = "read", Entity = "warehouse" }]
            }
        }
    };

    private static ErpEntity Entity(string id, string name) => new()
    {
        Id = id,
        Name = name,
        DisplayName = name,
        Display = new ErpEntityDisplay { Primary = "name" },
        Fields =
        [
            new ErpField
            {
                Id = "name",
                Name = "name",
                DisplayName = "Name",
                Type = "string",
                Required = true
            }
        ]
    };

    private sealed record RuntimeUserLinkFixture(
        ResolvedTenantEnvironment Tenant,
        RuntimeDatabase RuntimeDatabase,
        RuntimeRecordStore Records,
        RuntimeUserStore Users,
        RuntimeUserContext Owner,
        string Suffix)
    {
        public async Task<long> CreateRecordAsync(string entity, string name)
        {
            var record = await Records.CreateRecordAsync(
                Tenant,
                Owner,
                entity,
                RuntimeJson.JsonObject(("name", $"{name} {Suffix}")));
            return Convert.ToInt64(record["id"]);
        }

        public async Task<RuntimeUserContext> ActivateUserAsync(string role, string discriminator)
        {
            var email = $"{discriminator}-{Suffix}@example.test";
            var invite = await Users.InviteUserAsync(
                Tenant,
                Owner,
                email,
                discriminator,
                [role],
                employeeRecordId: null,
                TimeSpan.FromDays(1));
            invite.ActivationToken.Should().NotBeNullOrWhiteSpace();
            (await Users.ActivateInvitedUserAsync(
                Tenant,
                invite.ActivationToken!,
                email,
                discriminator,
                "RuntimeUserPass123!")).Should().BeTrue();
            return (await Users.GetActiveUserContextAsync(Tenant, invite.UserId))!;
        }
    }
}
