using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeConfigurationValidationTests
{
    [Fact]
    public void Development_allows_local_defaults()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateConfiguration(new Dictionary<string, string?>
            {
                ["ControlPlane:BaseUrl"] = "",
                ["ControlPlane:InternalSecret"] = "",
                ["Runtime:InternalSecret"] = "",
                ["Runtime:SessionSecret"] = "local-runtime-session-secret-change-me",
                ["RuntimeDatabase:TenantConnectionStringTemplate"] = "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database={database}"
            }),
            new TestHostEnvironment("Development"));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Production_rejects_missing_required_values()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateConfiguration(new Dictionary<string, string?>()),
            new TestHostEnvironment("Production"));

        result.IsValid.Should().BeFalse();
        result.Errors.Select(error => error.Key).Should().Contain([
            "ControlPlane__BaseUrl",
            "ControlPlane__InternalSecret",
            "Runtime__InternalSecret",
            "Runtime__SessionSecret",
            "RuntimeDeploymentBroker__Secret",
            "RuntimeDeploymentBroker__LifecycleMachineAttestationSecret",
            "RuntimeDeploymentBroker__LifecycleSupervisorAttestationSecret",
            "RuntimeDatabase__CredentialMode",
            "TenantDatabaseCredentialStore__Mode",
            "RuntimeBackups__Mode",
            "RuntimeBackups__AdminConnectionString",
            "RuntimeBackups__RequireBeforePromotion",
            "RuntimeBackups__StorageMode",
            "RuntimeDeployments__LockTtlMinutes"
        ]);
    }

    [Fact]
    public void Production_accepts_explicit_safe_configuration_without_auto_provisioning()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeDatabase:AutoProvisionDatabases"] = "false"
            }),
            new TestHostEnvironment("Production"));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Production_requires_admin_connection_when_auto_provisioning_is_enabled()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeDatabase:AutoProvisionDatabases"] = "true",
                ["RuntimeDatabase:AdminConnectionString"] = ""
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Should().Contain(error =>
            error.Key == "RuntimeDatabase__AdminConnectionString" &&
            error.Message.Contains("must be configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_requires_a_dedicated_backup_restore_admin_connection()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeBackups:AdminConnectionString"] = ""
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Should().Contain(error =>
            error.Key == "RuntimeBackups__AdminConnectionString" &&
            error.Message.Contains("must be configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rejects_the_legacy_tenant_connection_template()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeDatabase:TenantConnectionStringTemplate"] = "Host=tenant-db.internal.example;Database={database};Username=legacy;Password=legacy-password"
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Should().Contain(error =>
            error.Key == "RuntimeDatabase__TenantConnectionStringTemplate" &&
            error.Message.Contains("must be omitted", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rejects_in_memory_tenant_database_credential_store()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["TenantDatabaseCredentialStore:Mode"] = "InMemory"
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Should().Contain(error =>
            error.Key == "TenantDatabaseCredentialStore__Mode" &&
            error.Message.Contains("ControlPlaneEncrypted", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rejects_local_file_backup_storage()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeBackups:StorageMode"] = "LocalFile"
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Should().Contain(error =>
            error.Key == "RuntimeBackups__StorageMode" &&
            error.Message.Contains("S3Compatible", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rejects_missing_s3_backup_storage_values()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeBackups:S3:Bucket"] = "",
                ["RuntimeBackups:S3:AccessKeyId"] = "<set-in-secret-manager>",
                ["RuntimeBackups:S3:SecretAccessKey"] = "<set-in-secret-manager>"
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Select(error => error.Key).Should().Contain([
            "RuntimeBackups__S3__Bucket",
            "RuntimeBackups__S3__AccessKeyId",
            "RuntimeBackups__S3__SecretAccessKey"
        ]);
    }

    [Fact]
    public void Production_rejects_placeholder_secrets_and_local_database_targets()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["ControlPlane:InternalSecret"] = "change-me",
                ["Runtime:InternalSecret"] = "local-smoke-secret",
                ["Runtime:SessionSecret"] = "local-runtime-session-secret-change-me",
                ["RuntimeDatabase:AutoProvisionDatabases"] = "true",
                ["RuntimeDatabase:AdminConnectionString"] = "Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=postgres",
                ["RuntimeBackups:AdminConnectionString"] = "Host=127.0.0.1;Port=55432;Database=postgres;Username=backup;Password=backup-password"
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Select(error => error.Key).Should().Contain([
            "ControlPlane__InternalSecret",
            "Runtime__InternalSecret",
            "Runtime__SessionSecret",
            "RuntimeDatabase__AdminConnectionString",
            "RuntimeBackups__AdminConnectionString"
        ]);
    }

    [Fact]
    public void Production_rejects_placeholder_low_entropy_or_shared_lifecycle_verifier_keys()
    {
        var shared = "change-me-change-me-change-me-change-me-change-me";
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeDeploymentBroker:LifecycleMachineAttestationSecret"] = shared,
                ["RuntimeDeploymentBroker:LifecycleSupervisorAttestationSecret"] = shared
            }),
            new TestHostEnvironment("Production"));

        result.IsValid.Should().BeFalse();
        result.Errors.Select(error => error.Key).Should().Contain([
            "RuntimeDeploymentBroker__LifecycleMachineAttestationSecret",
            "RuntimeDeploymentBroker__LifecycleSupervisorAttestationSecret"
        ]);
    }

    [Fact]
    public void Production_rejects_env_example_replace_with_lifecycle_key_prefixes()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeDeploymentBroker:LifecycleMachineAttestationSecret"] =
                    "replace-with-independent-high-entropy-machine-key",
                ["RuntimeDeploymentBroker:LifecycleSupervisorAttestationSecret"] =
                    "replace-with-independent-high-entropy-supervisor-key"
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Select(error => error.Key).Should().Contain([
            "RuntimeDeploymentBroker__LifecycleMachineAttestationSecret",
            "RuntimeDeploymentBroker__LifecycleSupervisorAttestationSecret"
        ]);
    }

    [Fact]
    public void Production_rejects_embedded_placeholders_in_backup_admin_connection()
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeBackups:AdminConnectionString"] = "Host=<backup-db-host>;Database=postgres;Username=<backup-user>;Password=<backup-password>"
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Should().Contain(error =>
            error.Key == "RuntimeBackups__AdminConnectionString" &&
            error.Message.Contains("placeholder", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Production_rejects_invalid_deployment_lock_ttl(string value)
    {
        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["RuntimeDeployments:LockTtlMinutes"] = value
            }),
            new TestHostEnvironment("Production"));

        result.Errors.Should().Contain(error =>
            error.Key == "RuntimeDeployments__LockTtlMinutes" &&
            error.Message.Contains("positive", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_errors_do_not_echo_secret_values()
    {
        const string secretValue = "short-secret-value";

        var result = RuntimeConfigurationValidator.Validate(
            CreateProductionConfiguration(new Dictionary<string, string?>
            {
                ["Runtime:SessionSecret"] = secretValue
            }),
            new TestHostEnvironment("Production"));

        result.IsValid.Should().BeFalse();
        string.Join(Environment.NewLine, result.Errors.Select(error => $"{error.Key}: {error.Message}"))
            .Should()
            .NotContain(secretValue);
    }

    [Fact]
    public void Internal_secret_authorization_fails_closed_when_configured_secret_is_missing()
    {
        RuntimeRouteHelpers.IsInternalSecretAuthorized("provided", null).Should().BeFalse();
        RuntimeRouteHelpers.IsInternalSecretAuthorized("provided", "").Should().BeFalse();
        RuntimeRouteHelpers.IsInternalSecretAuthorized(null, "configured").Should().BeFalse();
    }

    [Fact]
    public void Internal_secret_authorization_accepts_only_exact_secret()
    {
        RuntimeRouteHelpers.IsInternalSecretAuthorized("runtime-secret", "runtime-secret").Should().BeTrue();
        RuntimeRouteHelpers.IsInternalSecretAuthorized("wrong-secret", "runtime-secret").Should().BeFalse();
        RuntimeRouteHelpers.IsInternalSecretAuthorized("runtime-secret-extra", "runtime-secret").Should().BeFalse();
    }

    private static IConfiguration CreateProductionConfiguration(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["ControlPlane:BaseUrl"] = "https://control-plane.internal.example",
            ["ControlPlane:InternalSecret"] = "control-plane-secret-that-is-long-enough",
            ["Runtime:InternalSecret"] = "runtime-internal-secret-that-is-long-enough",
            ["RuntimeDeploymentBroker:Secret"] = "api-runtime-broker-secret-that-is-long-enough",
            ["RuntimeDeploymentBroker:LifecycleMachineAttestationSecret"] = "M7!vQ2#xL9@pR4$kT8^zN5&wC1*eH6?yS3+uF0=gb",
            ["RuntimeDeploymentBroker:LifecycleSupervisorAttestationSecret"] = "J4@dW8!mK2#sP7$yV1^qT6&nR9*cL3?xA5+fH0=uz",
            ["Runtime:SessionSecret"] = "runtime-session-secret-that-is-at-least-thirty-two-characters",
            ["RuntimeDatabase:CredentialMode"] = "Provisioned",
            ["TenantDatabaseCredentialStore:Mode"] = "ControlPlaneEncrypted",
            ["RuntimeBackups:Mode"] = "Required",
            ["RuntimeBackups:AdminConnectionString"] = "Host=tenant-db.internal.example;Port=5432;Database=postgres;Username=backup_admin;Password=backup-admin-password",
            ["RuntimeBackups:RequireBeforePromotion"] = "true",
            ["RuntimeBackups:StorageMode"] = "S3Compatible",
            ["RuntimeBackups:S3:Bucket"] = "tenant-db-backups",
            ["RuntimeBackups:S3:Region"] = "eu-central-1",
            ["RuntimeBackups:S3:Endpoint"] = "https://object-storage.internal.example",
            ["RuntimeBackups:S3:AccessKeyId"] = "backup-access-key",
            ["RuntimeBackups:S3:SecretAccessKey"] = "backup-secret-key-that-is-long-enough",
            ["RuntimeBackups:S3:Prefix"] = "tenant-db-backups/",
            ["RuntimeDeployments:LockTtlMinutes"] = "15"
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return CreateConfiguration(values);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "ErpGenerator.Runtime.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
