using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantDatabaseConnectionFactoryTests
{
    [Fact]
    public void It_uses_control_plane_database_name_instead_of_tenant_slug()
    {
        var factory = new TenantDatabaseConnectionFactory(Options.Create(new RuntimeDatabaseOptions
        {
            TenantConnectionStringTemplate = "Host=localhost;Username=postgres;Password=postgres;Database=postgres"
        }));
        var tenant = new ResolvedTenantEnvironment(
            "1",
            "stable-id",
            "acme",
            "acme.erpgenerator.example",
            TenantEnvironmentKind.Erp,
            "erp_tenant_123e4567_staging",
            null,
            "http://acme.erpgenerator.example");

        var connectionString = factory.CreateConnectionString(tenant);

        connectionString.Should().Contain("Database=erp_tenant_123e4567_staging");
        connectionString.Should().NotContain("Database=acme");
    }

    [Fact]
    public void It_prefers_explicit_tenant_connection_string_for_external_targets()
    {
        var factory = new TenantDatabaseConnectionFactory(Options.Create(new RuntimeDatabaseOptions
        {
            TenantConnectionStringTemplate = "Host=localhost;Database=postgres"
        }));
        var tenant = new ResolvedTenantEnvironment(
            "1",
            "stable-id",
            "acme",
            "acme.erpgenerator.example",
            TenantEnvironmentKind.Erp,
            "erp_tenant_123e4567_staging",
            "Host=db.example;Database=tenant_db;Username=runtime",
            "http://acme.erpgenerator.example");

        factory.CreateConnectionString(tenant).Should().Be("Host=db.example;Database=tenant_db;Username=runtime");
    }

    [Fact]
    public void It_accepts_postgres_uri_templates_for_local_runtime_configuration()
    {
        var factory = new TenantDatabaseConnectionFactory(Options.Create(new RuntimeDatabaseOptions
        {
            TenantConnectionStringTemplate = "postgres://runtime:secret@localhost:55432/{database}"
        }));
        var tenant = new ResolvedTenantEnvironment(
            "1",
            "stable-id",
            "acme",
            "acme.erpgenerator.example",
            TenantEnvironmentKind.Erp,
            "erp_tenant_123e4567_staging",
            null,
            "http://acme.erpgenerator.example");

        var connectionString = factory.CreateConnectionString(tenant);

        connectionString.Should().Contain("Host=localhost");
        connectionString.Should().Contain("Port=55432");
        connectionString.Should().Contain("Database=erp_tenant_123e4567_staging");
        connectionString.Should().Contain("Username=runtime");
        connectionString.Should().Contain("Password=secret");
    }

    [Fact]
    public void It_accepts_explicit_postgres_uri_targets()
    {
        var factory = new TenantDatabaseConnectionFactory(Options.Create(new RuntimeDatabaseOptions
        {
            TenantConnectionStringTemplate = "Host=localhost;Database=postgres"
        }));
        var tenant = new ResolvedTenantEnvironment(
            "1",
            "stable-id",
            "acme",
            "acme.erpgenerator.example",
            TenantEnvironmentKind.Erp,
            "erp_tenant_123e4567_staging",
            "postgresql://runtime:secret@db.example:5433/external_tenant",
            "http://acme.erpgenerator.example");

        var connectionString = factory.CreateConnectionString(tenant);

        connectionString.Should().Contain("Host=db.example");
        connectionString.Should().Contain("Port=5433");
        connectionString.Should().Contain("Database=external_tenant");
        connectionString.Should().Contain("Username=runtime");
        connectionString.Should().Contain("Password=secret");
    }

    [Fact]
    public void Scheduler_connections_are_non_pooled_per_tenant_while_other_purposes_preserve_configuration()
    {
        var options = Options.Create(new RuntimeDatabaseOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=admin;Pooling=true;Maximum Pool Size=17",
            CredentialMode = TenantDatabaseCredentialMode.Provisioned
        });
        var tenant = new ResolvedTenantEnvironment(
            "1",
            "stable-id",
            "acme",
            "acme.erpgenerator.example",
            TenantEnvironmentKind.Erp,
            "erp_tenant_123e4567_staging",
            null,
            "http://acme.erpgenerator.example");
        var credentials = new InMemoryTenantDatabaseCredentialStore();
        credentials.Store(new TenantDatabaseCredential(
            tenant.TenantStableId,
            tenant.Environment,
            tenant.DatabaseName,
            "runtime_secret",
            $"Host=localhost;Database={tenant.DatabaseName};Username=runtime_secret;Pooling=true;Maximum Pool Size=11",
            "secret-ref"));
        var factory = new TenantDatabaseConnectionFactory(options, credentials);

        var scheduler = new NpgsqlConnectionStringBuilder(factory.CreateConnectionString(
            tenant,
            TenantDatabaseConnectionPurpose.Scheduler));
        var provisioning = new NpgsqlConnectionStringBuilder(factory.CreateConnectionString(
            tenant,
            TenantDatabaseConnectionPurpose.Provisioning));
        var runtime = new NpgsqlConnectionStringBuilder(factory.CreateConnectionString(tenant));

        scheduler.Database.Should().Be(tenant.DatabaseName);
        scheduler.Username.Should().Be("runtime_secret");
        scheduler.Pooling.Should().BeFalse("one normal pool per tenant would retain unbounded physical connections");
        provisioning.Username.Should().Be("admin");
        provisioning.Pooling.Should().BeTrue();
        provisioning.MaxPoolSize.Should().Be(17);
        runtime.Username.Should().Be("runtime_secret");
        runtime.Pooling.Should().BeTrue();
        runtime.MaxPoolSize.Should().Be(11);
    }
}
