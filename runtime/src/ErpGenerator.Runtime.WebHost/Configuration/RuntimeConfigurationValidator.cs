using Microsoft.Extensions.Hosting;
using ErpGenerator.Runtime.Shared.Deployment;

public static class RuntimeConfigurationValidator
{
    private static readonly string[] UnsafeSecretValues =
    [
        "change-me",
        "changeme",
        "dev",
        "development",
        "local",
        "local-secret",
        "local-smoke-secret",
        "local-runtime-session-secret-change-me",
        "local-smoke-runtime-session-secret-change-me",
        "secret",
        "test"
    ];

    public static RuntimeConfigurationValidationResult Validate(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var errors = new List<RuntimeConfigurationValidationError>();
        var isDevelopment = environment.IsDevelopment();

        if (isDevelopment)
        {
            return new RuntimeConfigurationValidationResult(errors);
        }

        RequireAbsoluteHttpUrl(configuration, "ControlPlane:BaseUrl", errors);
        RequireSecret(configuration, "ControlPlane:InternalSecret", minLength: 16, errors);
        RequireSecret(configuration, "Runtime:InternalSecret", minLength: 16, errors);
        RequireSecret(configuration, "RuntimeDeploymentBroker:Secret", minLength: 16, errors);
        RequireSecret(configuration, "RuntimeDeploymentBroker:LifecycleMachineAttestationSecret", minLength: 32, errors);
        RequireSecret(configuration, "RuntimeDeploymentBroker:LifecycleSupervisorAttestationSecret", minLength: 32, errors);
        RequireDistinctValues(
            configuration,
            "RuntimeDeploymentBroker:LifecycleMachineAttestationSecret",
            "RuntimeDeploymentBroker:LifecycleSupervisorAttestationSecret",
            errors);
        RequireLifecycleVerifierKeyStrength(configuration, errors);
        RequireSecret(configuration, "Runtime:SessionSecret", minLength: 32, errors);
        RequireProvisionedCredentialMode(configuration, errors);
        RequireDurableCredentialStore(configuration, errors);
        RequireBackupGuardrails(configuration, errors);
        RequireDeploymentGuardrails(configuration, errors);

        RejectLegacyTenantConnectionTemplate(configuration, errors);

        var autoProvision = configuration.GetValue<bool>("RuntimeDatabase:AutoProvisionDatabases");

        if (autoProvision)
        {
            var adminConnectionString = configuration["RuntimeDatabase:AdminConnectionString"];
            RequireValue(configuration, "RuntimeDatabase:AdminConnectionString", errors);
            RejectLocalDatabaseConnection("RuntimeDatabase:AdminConnectionString", adminConnectionString, errors);
        }
        else
        {
            RejectLocalDatabaseConnection(
                "RuntimeDatabase:AdminConnectionString",
                configuration["RuntimeDatabase:AdminConnectionString"],
                errors);
        }

        return new RuntimeConfigurationValidationResult(errors);
    }

    public static void ValidateOrThrow(IConfiguration configuration, IHostEnvironment environment)
    {
        var result = Validate(configuration, environment);
        if (!result.IsValid)
        {
            throw new RuntimeConfigurationException(result);
        }
    }

    private static void RequireValue(
        IConfiguration configuration,
        string key,
        List<RuntimeConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must be configured outside Development."));
        }
    }

    private static void RequireSecret(
        IConfiguration configuration,
        string key,
        int minLength,
        List<RuntimeConfigurationValidationError> errors)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must be configured outside Development."));
            return;
        }

        if (value.Trim().Length < minLength)
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), $"must be at least {minLength} characters outside Development."));
        }

        if (IsUnsafeSecretValue(value))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must not use a development placeholder value outside Development."));
        }

        if (value.Trim().StartsWith("<", StringComparison.Ordinal))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must not use a placeholder value outside Development."));
        }
    }

    private static void RequireProvisionedCredentialMode(
        IConfiguration configuration,
        List<RuntimeConfigurationValidationError> errors)
    {
        var value = configuration["RuntimeDatabase:CredentialMode"];
        if (!string.Equals(value, "Provisioned", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey("RuntimeDatabase:CredentialMode"),
                "must be Provisioned outside Development so normal runtime traffic does not use shared provisioning credentials."));
        }
    }

    private static void RequireDistinctValues(
        IConfiguration configuration,
        string leftKey,
        string rightKey,
        List<RuntimeConfigurationValidationError> errors)
    {
        var left = configuration[leftKey]?.Trim();
        var right = configuration[rightKey]?.Trim();
        if (!string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left, right, StringComparison.Ordinal))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey(rightKey),
                $"must be distinct from {DisplayKey(leftKey)}."));
        }
    }

    private static void RequireLifecycleVerifierKeyStrength(
        IConfiguration configuration,
        List<RuntimeConfigurationValidationError> errors)
    {
        var machine = configuration["RuntimeDeploymentBroker:LifecycleMachineAttestationSecret"];
        var supervisor = configuration["RuntimeDeploymentBroker:LifecycleSupervisorAttestationSecret"];
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(supervisor))
        {
            return;
        }

        try
        {
            using var _ = new RuntimeLifecyclePrivilegeAttestationAuthority(machine, supervisor);
        }
        catch (ArgumentException)
        {
            errors.Add(new RuntimeConfigurationValidationError(
                "RuntimeDeploymentBroker__LifecycleMachineAttestationSecret",
                "and the supervisor key must be distinct, non-placeholder, high-entropy secret material."));
            errors.Add(new RuntimeConfigurationValidationError(
                "RuntimeDeploymentBroker__LifecycleSupervisorAttestationSecret",
                "and the machine key must be distinct, non-placeholder, high-entropy secret material."));
        }
    }

    private static void RequireDurableCredentialStore(
        IConfiguration configuration,
        List<RuntimeConfigurationValidationError> errors)
    {
        var value = configuration["TenantDatabaseCredentialStore:Mode"];
        if (!string.Equals(value, "ControlPlaneEncrypted", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey("TenantDatabaseCredentialStore:Mode"),
                "must be ControlPlaneEncrypted outside Development so provisioned tenant app credentials survive process restarts."));
        }
    }

    private static void RequireBackupGuardrails(
        IConfiguration configuration,
        List<RuntimeConfigurationValidationError> errors)
    {
        RequireSecret(configuration, "RuntimeBackups:AdminConnectionString", minLength: 16, errors);
        RejectEmbeddedPlaceholderValue(configuration, "RuntimeBackups:AdminConnectionString", errors);
        RejectLocalDatabaseConnection(
            "RuntimeBackups:AdminConnectionString",
            configuration["RuntimeBackups:AdminConnectionString"],
            errors);

        var mode = configuration["RuntimeBackups:Mode"];
        if (!string.Equals(mode, "Required", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey("RuntimeBackups:Mode"),
                "must be Required outside Development."));
        }

        if (!configuration.GetValue<bool>("RuntimeBackups:RequireBeforePromotion"))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey("RuntimeBackups:RequireBeforePromotion"),
                "must be true outside Development."));
        }

        var storageMode = configuration["RuntimeBackups:StorageMode"];
        if (!string.Equals(storageMode, "S3Compatible", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey("RuntimeBackups:StorageMode"),
                "must be S3Compatible outside Development when pre-promotion backups are required."));
            return;
        }

        RequireNonPlaceholderValue(configuration, "RuntimeBackups:S3:Bucket", errors);
        RequireNonPlaceholderValue(configuration, "RuntimeBackups:S3:Region", errors);
        RequireAbsoluteHttpUrl(configuration, "RuntimeBackups:S3:Endpoint", errors);
        RequireSecret(configuration, "RuntimeBackups:S3:AccessKeyId", minLength: 8, errors);
        RequireSecret(configuration, "RuntimeBackups:S3:SecretAccessKey", minLength: 16, errors);
        RequireNonPlaceholderValue(configuration, "RuntimeBackups:S3:Prefix", errors);
    }

    private static void RejectLegacyTenantConnectionTemplate(
        IConfiguration configuration,
        List<RuntimeConfigurationValidationError> errors)
    {
        if (!string.IsNullOrWhiteSpace(configuration["RuntimeDatabase:TenantConnectionStringTemplate"]))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey("RuntimeDatabase:TenantConnectionStringTemplate"),
                "must be omitted outside Development; tenant runtime credentials must resolve through the durable credential store."));
        }
    }

    private static void RejectEmbeddedPlaceholderValue(
        IConfiguration configuration,
        string key,
        List<RuntimeConfigurationValidationError> errors)
    {
        var value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value) &&
            (value.Contains('<', StringComparison.Ordinal) || value.Contains('>', StringComparison.Ordinal)))
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey(key),
                "must not contain placeholder values outside Development."));
        }
    }

    private static void RequireDeploymentGuardrails(
        IConfiguration configuration,
        List<RuntimeConfigurationValidationError> errors)
    {
        if (configuration.GetValue<int>("RuntimeDeployments:LockTtlMinutes") <= 0)
        {
            errors.Add(new RuntimeConfigurationValidationError(
                DisplayKey("RuntimeDeployments:LockTtlMinutes"),
                "must be a positive number outside Development."));
        }
    }

    private static void RequireAbsoluteHttpUrl(
        IConfiguration configuration,
        string key,
        List<RuntimeConfigurationValidationError> errors)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must be configured outside Development."));
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must be an absolute HTTP or HTTPS URL."));
        }
    }

    private static void RequireNonPlaceholderValue(
        IConfiguration configuration,
        string key,
        List<RuntimeConfigurationValidationError> errors)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must be configured outside Development."));
            return;
        }

        if (value.Trim().StartsWith("<", StringComparison.Ordinal) || IsUnsafeSecretValue(value))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must not use a placeholder value outside Development."));
        }
    }

    private static void RejectLocalDatabaseConnection(
        string key,
        string? connectionString,
        List<RuntimeConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var normalized = connectionString.Trim().ToLowerInvariant();
        if (normalized.Contains("host=localhost", StringComparison.Ordinal) ||
            normalized.Contains("host=127.0.0.1", StringComparison.Ordinal) ||
            normalized.Contains("://localhost", StringComparison.Ordinal) ||
            normalized.Contains("://127.0.0.1", StringComparison.Ordinal))
        {
            errors.Add(new RuntimeConfigurationValidationError(DisplayKey(key), "must not target localhost outside Development."));
        }
    }

    private static bool IsUnsafeSecretValue(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return UnsafeSecretValues.Contains(normalized, StringComparer.Ordinal) ||
               normalized.Contains("change-me", StringComparison.Ordinal) ||
               normalized.Contains("changeme", StringComparison.Ordinal) ||
               normalized.Contains("replace-with", StringComparison.Ordinal) ||
               normalized.Contains("placeholder", StringComparison.Ordinal);
    }

    private static string DisplayKey(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}
