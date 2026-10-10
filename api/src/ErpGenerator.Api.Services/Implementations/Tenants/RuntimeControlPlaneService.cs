using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class RuntimeControlPlaneService(
    ErpGeneratorDbContext dbContext,
    IOptions<RuntimeAddressOptions> runtimeOptions,
    IOptions<InternalApiOptions> internalOptions) : IRuntimeControlPlaneService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeTenantEnvironmentViewModel?> ResolveAsync(
        string host,
        string? environment,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeHost(host);
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var query = dbContext.TenantEnvironments.AsNoTracking()
            .Include(value => value.Tenant)
            .Where(value => value.Host == normalizedHost && value.Tenant.Status == "active" && value.Environment == "erp");
        if (normalizedEnvironment is not null)
            query = query.Where(value => value.Environment == normalizedEnvironment);
        var result = await query.SingleOrDefaultAsync(cancellationToken);
        return result is null ? null : Map(result);
    }

    public async Task<RuntimeScheduleTargetsViewModel> ListScheduleTargetsAsync(
        int page,
        int limit,
        CancellationToken cancellationToken)
    {
        if (page < 1 || limit is < 1 or > 100) throw new ArgumentException("A valid bounded page is required.");
        var query = dbContext.TenantEnvironments.AsNoTracking()
            .Include(value => value.Tenant)
            .Where(value => value.Status == "ready" && value.Environment == "erp" && value.Tenant.Status == "active")
            .OrderBy(value => value.Tenant.StableId).ThenBy(value => value.Environment);
        var rows = await query.Skip((page - 1) * limit).Take(limit + 1).ToListAsync(cancellationToken);
        var hasNextPage = rows.Count > limit;
        return new RuntimeScheduleTargetsViewModel(page, hasNextPage, rows.Take(limit).Select(Map).ToArray());
    }

    public async Task StoreCredentialAsync(
        StoreTenantDatabaseCredentialInputModel input,
        CancellationToken cancellationToken)
    {
        var environment = NormalizeEnvironment(input.Environment) ?? throw new ArgumentException("Environment is required.");
        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(
            value => value.StableId == input.TenantStableId.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException("Tenant was not found.");
        var tenantEnvironment = await dbContext.TenantEnvironments.SingleOrDefaultAsync(
            value => value.TenantId == tenant.Id && value.Environment == environment && value.DatabaseName == input.DatabaseName,
            cancellationToken) ?? throw new KeyNotFoundException("Tenant environment was not found.");
        if (input.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(input.Password) || string.IsNullOrWhiteSpace(input.Username))
            throw new ArgumentException("A complete tenant database credential is required.");

        var secretReference = BuildSecretReference(tenant.StableId, environment, tenantEnvironment.DatabaseName);
        var payload = JsonSerializer.Serialize(new StoredCredential(
            input.Username.Trim(), input.Host.Trim(), input.Port, input.Database.Trim(), input.Password, input.SslMode.Trim()), JsonOptions);
        var encrypted = Encrypt(secretReference, payload);
        var entity = await dbContext.TenantDatabaseSecrets.SingleOrDefaultAsync(
            value => value.SecretReference == secretReference, cancellationToken);
        if (entity is null)
        {
            entity = new TenantDatabaseSecret
            {
                SecretReference = secretReference,
                TenantStableId = tenant.StableId,
                Environment = environment,
                DatabaseName = tenantEnvironment.DatabaseName
            };
            dbContext.TenantDatabaseSecrets.Add(entity);
        }
        entity.Ciphertext = encrypted.Ciphertext;
        entity.Nonce = encrypted.Nonce;
        entity.AuthenticationTag = encrypted.Tag;
        tenantEnvironment.CredentialSecretReference = secretReference;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<TenantDatabaseCredentialViewModel?> ResolveCredentialAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        var normalized = secretReference.Trim();
        var entity = await dbContext.TenantDatabaseSecrets.AsNoTracking()
            .SingleOrDefaultAsync(value => value.SecretReference == normalized, cancellationToken);
        if (entity is null) return null;
        var stored = JsonSerializer.Deserialize<StoredCredential>(
            Decrypt(normalized, entity.Ciphertext, entity.Nonce, entity.AuthenticationTag), JsonOptions)
            ?? throw new InvalidOperationException("Stored tenant credential is invalid.");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = stored.Host,
            Port = stored.Port,
            Database = stored.Database,
            Username = stored.Username,
            Password = stored.Password
        };
        if (Enum.TryParse<Npgsql.SslMode>(stored.SslMode, true, out var sslMode)) builder.SslMode = sslMode;
        return new TenantDatabaseCredentialViewModel(
            entity.SecretReference, entity.TenantStableId, entity.Environment, entity.DatabaseName,
            stored.Username, builder.ConnectionString);
    }

    public async Task RecordDeploymentEventAsync(RuntimeDeploymentEventInputModel input, CancellationToken cancellationToken)
    {
        var environment = await FindEnvironmentAsync(input.Host, input.Environment, cancellationToken);
        dbContext.RuntimeDeploymentEvents.Add(new RuntimeDeploymentEvent
        {
            TenantEnvironmentId = environment.Id,
            Type = Required(input.Type, 80),
            Message = Required(input.Message, 4000),
            Details = Bounded(input.Details, 32000)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDeploymentStatusAsync(RuntimeDeploymentStatusInputModel input, CancellationToken cancellationToken)
    {
        var environment = await FindEnvironmentAsync(input.Host, input.Environment, cancellationToken);
        environment.Status = Required(input.Status, 32).ToLowerInvariant();
        environment.RuntimeUrl = Bounded(input.RuntimeUrl, 500) ?? environment.RuntimeUrl;
        environment.OwnerActivationUrl = Bounded(input.ActivationUrl, 500) ?? environment.OwnerActivationUrl;
        environment.ActiveBundleId = Bounded(input.BundleId, 120) ?? environment.ActiveBundleId;
        environment.ActiveBundleVersion = Bounded(input.BundleVersion, 64) ?? environment.ActiveBundleVersion;
        environment.ActiveBundleHash = Bounded(input.BundleHash, 64) ?? environment.ActiveBundleHash;
        var deployment = await dbContext.Deployments
            .Where(value => value.TenantId == environment.TenantId && value.Environment == environment.Environment)
            .OrderByDescending(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (deployment is not null)
        {
            deployment.Status = environment.Status;
            deployment.FailureMessage = Bounded(input.Message, 4000);
            deployment.DeployedBundleId = environment.ActiveBundleId;
            deployment.DeployedBundleVersion = environment.ActiveBundleVersion;
            deployment.DeployedBundleHash = environment.ActiveBundleHash;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateOwnerActivationStatusAsync(RuntimeOwnerActivationStatusInputModel input, CancellationToken cancellationToken)
    {
        var environment = await FindEnvironmentAsync(input.Host, input.Environment, cancellationToken);
        var normalizedEmail = input.Email.Trim().ToLowerInvariant();
        var activation = await dbContext.OwnerActivations.SingleOrDefaultAsync(
            value => value.TenantEnvironmentId == environment.Id && value.Email == normalizedEmail, cancellationToken);
        if (activation is null)
        {
            activation = new OwnerActivation { TenantEnvironmentId = environment.Id, Email = normalizedEmail };
            dbContext.OwnerActivations.Add(activation);
        }
        activation.Status = Required(input.Status, 32).ToLowerInvariant();
        activation.ActivatedAt = input.ActivatedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SubmitFeedbackAsync(RuntimeFeedbackInputModel input, CancellationToken cancellationToken)
    {
        var environment = await FindEnvironmentAndVerifyTenantAsync(input.Host, input.Environment, input.TenantStableId, input.TenantSlug, cancellationToken);
        dbContext.RuntimeFeedback.Add(new RuntimeFeedback
        {
            TenantEnvironmentId = environment.Id,
            RuntimeUserId = Required(input.RuntimeUserId, 160),
            RuntimeUserEmail = Required(input.RuntimeUserEmail, 320).ToLowerInvariant(),
            Category = Bounded(input.Category, 80),
            Message = Required(input.Message, 8000),
            EntityKey = Bounded(input.EntityKey, 160),
            RecordId = Bounded(input.RecordId, 160),
            PagePath = Bounded(input.PagePath, 500),
            MetadataJson = JsonSerializer.Serialize(input.Metadata ?? new Dictionary<string, object?>(), JsonOptions)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SubmitUsageAsync(RuntimeUsageEventInputModel input, CancellationToken cancellationToken)
    {
        var environment = await FindEnvironmentAndVerifyTenantAsync(input.Host, input.Environment, input.TenantStableId, input.TenantSlug, cancellationToken);
        dbContext.RuntimeUsageEvents.Add(new RuntimeUsageEvent
        {
            TenantEnvironmentId = environment.Id,
            RuntimeUserId = Required(input.RuntimeUserId, 160),
            RuntimeUserEmail = Required(input.RuntimeUserEmail, 320).ToLowerInvariant(),
            RuntimeUserRole = Required(input.RuntimeUserRole, 80),
            EventKind = Required(input.EventKind, 80),
            OccurredAt = input.OccurredAt,
            EntityKey = Bounded(input.EntityKey, 160),
            RecordId = Bounded(input.RecordId, 160),
            WorkflowKey = Bounded(input.WorkflowKey, 160),
            ActionKey = Bounded(input.ActionKey, 160),
            ReportKey = Bounded(input.ReportKey, 160),
            PagePath = Bounded(input.PagePath, 500),
            MetadataJson = JsonSerializer.Serialize(input.Metadata ?? new Dictionary<string, object?>(), JsonOptions)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<TenantEnvironment> FindEnvironmentAsync(string host, string environment, CancellationToken cancellationToken) =>
        await dbContext.TenantEnvironments.Include(value => value.Tenant).SingleOrDefaultAsync(
            value => value.Host == NormalizeHost(host) && value.Environment == NormalizeEnvironment(environment), cancellationToken)
        ?? throw new KeyNotFoundException("Tenant environment was not found.");

    private async Task<TenantEnvironment> FindEnvironmentAndVerifyTenantAsync(
        string host, string environment, string stableId, string slug, CancellationToken cancellationToken)
    {
        var result = await FindEnvironmentAsync(host, environment, cancellationToken);
        if (result.Tenant.StableId != stableId.Trim() || result.Tenant.Slug != slug.Trim().ToLowerInvariant())
            throw new InvalidOperationException("Runtime tenant lineage did not match the control plane.");
        return result;
    }

    private RuntimeTenantEnvironmentViewModel Map(TenantEnvironment environment)
    {
        var runtimeUrl = environment.RuntimeUrl ?? $"{runtimeOptions.Value.Scheme}://{environment.Host}";
        var secretReference = environment.CredentialSecretReference ??
            BuildSecretReference(environment.Tenant.StableId, environment.Environment, environment.DatabaseName);
        return new RuntimeTenantEnvironmentViewModel(
            environment.TenantId.ToString(), environment.Tenant.StableId, environment.Tenant.Slug,
            environment.Host, environment.Environment, environment.DatabaseName, null, runtimeUrl,
            "secret_ref", secretReference, environment.Status);
    }

    private EncryptedPayload Encrypt(string secretReference, string plaintext)
    {
        var key = GetEncryptionKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plaintext), ciphertext, tag, Encoding.UTF8.GetBytes(secretReference));
        return new EncryptedPayload(Convert.ToBase64String(ciphertext), Convert.ToBase64String(nonce), Convert.ToBase64String(tag));
    }

    private string Decrypt(string secretReference, string ciphertext, string nonce, string tag)
    {
        var plaintext = new byte[Convert.FromBase64String(ciphertext).Length];
        using var aes = new AesGcm(GetEncryptionKey(), 16);
        aes.Decrypt(Convert.FromBase64String(nonce), Convert.FromBase64String(ciphertext), Convert.FromBase64String(tag), plaintext, Encoding.UTF8.GetBytes(secretReference));
        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] GetEncryptionKey()
    {
        try
        {
            var key = Convert.FromBase64String(internalOptions.Value.DatabaseSecretEncryptionKey);
            if (key.Length == 32) return key;
        }
        catch (FormatException) { }
        throw new InvalidOperationException("InternalApi__DatabaseSecretEncryptionKey must be a base64-encoded 32-byte key.");
    }

    private static string BuildSecretReference(string stableId, string environment, string databaseName) =>
        $"tenant-db:{stableId}:{environment}:{databaseName}";
    private static string NormalizeHost(string value) => value.Trim().Split(':')[0].ToLowerInvariant();
    private static string? NormalizeEnvironment(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "erp" => "erp",
        null or "" => null,
        _ => throw new ArgumentException("Environment must be erp.")
    };
    private static string Required(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A required value is missing.") : Bounded(value, max)!;
    private static string? Bounded(string? value, int max)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result)) return null;
        if (result.Length > max || result.Any(char.IsControl)) throw new ArgumentException("A value is invalid or too long.");
        return result;
    }

}
