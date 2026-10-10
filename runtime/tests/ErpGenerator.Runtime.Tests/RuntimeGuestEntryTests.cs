using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeGuestEntryTests
{
    [PostgresFact]
    public async Task Handoff_is_hashed_tenant_local_expiring_and_consumed_once()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var name = "erp_guest_entry_" + Guid.NewGuid().ToString("N")[..10];
        await TestPostgres.CreateDatabaseAsync(admin, name);
        try
        {
            var tenant = RuntimeTestTenant.Create("guest", "guest.localhost", name);
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var database = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await database.ApplyCompiledBundleAsync(tenant, await RuntimeBundleTestHelper.CompileSampleBundleAsync());
            var identity = Guid.NewGuid().ToString("N");
            var ticket = await database.CreateGuestEntryAsync(tenant, identity, "Guest", TimeSpan.FromMinutes(2));
            await using (var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "select token_hash from _runtime.guest_entry_tokens";
                var stored = (string)(await command.ExecuteScalarAsync())!;
                stored.Should().HaveLength(64).And.NotBe(ticket);
            }
            (await database.ConsumeGuestEntryAsync(tenant with { Host = "someone-else.localhost" }, ticket)).Should().BeNull();
            (await database.ConsumeGuestEntryAsync(tenant with { Environment = TenantEnvironmentKind.Production }, ticket)).Should().BeNull();
            (await database.ConsumeGuestEntryAsync(tenant with { TenantStableId = "another-tenant" }, ticket)).Should().BeNull();
            var user = await database.ConsumeGuestEntryAsync(tenant, ticket);
            user.Should().NotBeNull();
            user!.Email.Should().Be($"guest-{identity}@demo.invalid");
            (await database.ValidateLoginAsync(tenant, user.Email, "any-password")).Should().BeNull();
            (await database.ConsumeGuestEntryAsync(tenant, ticket)).Should().BeNull();
            var expired = await database.CreateGuestEntryAsync(tenant, identity, "Guest", TimeSpan.FromSeconds(-1));
            (await database.ConsumeGuestEntryAsync(tenant, expired)).Should().BeNull();
        }
        finally { await TestPostgres.DropDatabaseAsync(admin, name); }
    }
}
