using FluentAssertions;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Preparation;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantPreparationDatabaseResetterTests
{
    [Fact]
    public async Task Unknown_environment_is_rejected_before_any_database_execution()
    {
        var execution = new CapturingResetExecution();
        var lockExecution = new CapturingLockExecution();
        var stableId = "123e4567-e89b-12d3-a456-426614174000";
        var tenant = Tenant(stableId, (TenantEnvironmentKind)99, "erp_tenant_123e4567e89b12d3a45642661417_erp");
        var resetter = Resetter(execution, lockExecution);

        var act = () => resetter.ResetAsync(tenant);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Only a tenant ERP database can use the preparation operation fence.");
        lockExecution.Calls.Should().Be(0);
        execution.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("erp_tenant_other_staging")]
    [InlineData("ERP_TENANT_123E4567E89B12D3A45642661417_STAGING")]
    [InlineData("erp_tenant_123e4567e89b12d3a45642661417_staging;drop database postgres")]
    public async Task Database_name_must_exactly_match_immutable_stable_id(string databaseName)
    {
        var execution = new CapturingResetExecution();
        var lockExecution = new CapturingLockExecution();
        var tenant = Tenant(
            "123e4567-e89b-12d3-a456-426614174000",
            TenantEnvironmentKind.Erp,
            databaseName);

        var act = () => Resetter(execution, lockExecution).ResetAsync(tenant);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Tenant ERP database identity does not match its immutable stable id.");
        lockExecution.Calls.Should().Be(0);
        execution.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Missing_auto_provision_configuration_is_rejected_before_execution()
    {
        var execution = new CapturingResetExecution();
        var lockExecution = new CapturingLockExecution();
        var tenant = ValidTenant();
        var options = Options.Create(new RuntimeDatabaseOptions
        {
            AutoProvisionDatabases = false,
            AdminConnectionString = "Host=localhost;Database=postgres;Username=postgres;Password=secret"
        });
        var fence = new PostgresTenantPreparationOperationFence(options, lockExecution);
        var resetter = new PostgresTenantPreparationDatabaseResetter(
            options,
            new NoOpTenantDatabaseProvisioner(),
            new InMemoryTenantDatabaseCredentialStore(),
            fence,
            execution);

        var act = () => resetter.ResetAsync(tenant);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Automatic tenant database provisioning is required for ERP preparation operations.");
        lockExecution.Calls.Should().Be(0);
        execution.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Missing_admin_connection_is_rejected_before_execution()
    {
        var execution = new CapturingResetExecution();
        var lockExecution = new CapturingLockExecution();
        var tenant = ValidTenant();
        var options = Options.Create(new RuntimeDatabaseOptions
        {
            AutoProvisionDatabases = true,
            AdminConnectionString = " "
        });
        var fence = new PostgresTenantPreparationOperationFence(options, lockExecution);
        var resetter = new PostgresTenantPreparationDatabaseResetter(
            options,
            new NoOpTenantDatabaseProvisioner(),
            new InMemoryTenantDatabaseCredentialStore(),
            fence,
            execution);

        var act = () => resetter.ResetAsync(tenant);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("An administrative database connection is required for ERP preparation operations.");
        lockExecution.Calls.Should().Be(0);
        execution.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Valid_staging_identity_executes_once_and_result_contains_no_connection_secret()
    {
        var execution = new CapturingResetExecution();
        var lockExecution = new CapturingLockExecution();
        var tenant = ValidTenant();

        var result = await Resetter(execution, lockExecution).ResetAsync(tenant);

        lockExecution.Calls.Should().Be(1);
        execution.Calls.Should().Be(1);
        execution.Tenant.Should().Be(tenant);
        result.Status.Should().Be("reset");
        result.ToString().ToLowerInvariant().Should().NotContain("password");
        typeof(TenantPreparationDatabaseResetResult).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("database", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Name_policy_matches_control_plane_stable_id_contract()
    {
        TenantDatabaseNamePolicy.BuildExpected(
                "123e4567-e89b-12d3-a456-426614174000",
                TenantEnvironmentKind.Erp)
            .Should().Be("erp_tenant_123e4567e89b12d3a45642661417_erp");
        TenantDatabaseNamePolicy.BuildExpected(
                "123e4567-e89b-12d3-a456-426614174000",
                TenantEnvironmentKind.Erp)
            .Should().Be("erp_tenant_123e4567e89b12d3a45642661417_erp");
    }

    private static PostgresTenantPreparationDatabaseResetter Resetter(
        CapturingResetExecution execution,
        CapturingLockExecution lockExecution)
    {
        var options = Options.Create(new RuntimeDatabaseOptions
            {
                AutoProvisionDatabases = true,
                AdminConnectionString = "Host=localhost;Database=postgres;Username=postgres;Password=secret"
            });
        return new PostgresTenantPreparationDatabaseResetter(
            options,
            new NoOpTenantDatabaseProvisioner(),
            new InMemoryTenantDatabaseCredentialStore(),
            new PostgresTenantPreparationOperationFence(options, lockExecution),
            execution);
    }

    private static ResolvedTenantEnvironment ValidTenant()
    {
        const string stableId = "123e4567-e89b-12d3-a456-426614174000";
        return Tenant(
            stableId,
            TenantEnvironmentKind.Erp,
            TenantDatabaseNamePolicy.BuildExpected(stableId, TenantEnvironmentKind.Erp));
    }

    private static ResolvedTenantEnvironment Tenant(
        string stableId,
        TenantEnvironmentKind environment,
        string databaseName) =>
        new(
            "tenant-1",
            stableId,
            "acme",
            environment == TenantEnvironmentKind.Erp ? "staging.acme.erpgenerator.example" : "acme.erpgenerator.example",
            environment,
            databaseName,
            null,
            "https://staging.acme.erpgenerator.example");

    private sealed class CapturingResetExecution : ITenantPreparationDatabaseResetExecution
    {
        public int Calls { get; private set; }
        public ResolvedTenantEnvironment? Tenant { get; private set; }

        public Task ResetAsync(
            string adminConnectionString,
            ResolvedTenantEnvironment tenant,
            ITenantDatabaseProvisioner provisioner,
            ITenantDatabaseCredentialStore credentialStore,
            CancellationToken cancellationToken)
        {
            Calls++;
            Tenant = tenant;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLockExecution : ITenantPreparationAdvisoryLockExecution
    {
        public int Calls { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(
            string adminConnectionString,
            long advisoryLockKey,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IAsyncDisposable>(new NoOpHandle());
        }
    }

    private sealed class NoOpHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
