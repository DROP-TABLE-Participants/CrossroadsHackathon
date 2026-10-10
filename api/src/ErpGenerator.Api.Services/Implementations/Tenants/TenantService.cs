using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class TenantService(
    ErpGeneratorDbContext dbContext,
    IOptions<RuntimeAddressOptions> runtimeOptions) : ITenantService
{
    public async Task<TenantListViewModel> ListAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var tenants = await dbContext.Tenants.AsNoTracking()
            .AsSplitQuery()
            .Include(value => value.Environments)
            .Where(value => value.OwnerId == ownerId)
            .OrderByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        return new TenantListViewModel(tenants.Select(Map).ToArray());
    }

    public async Task<CreateTenantViewModel> CreateAsync(
        Guid ownerId,
        CreateTenantInputModel input,
        CancellationToken cancellationToken)
    {
        var companyName = (input.EnvironmentName ?? input.CompanyName)?.Trim() ?? string.Empty;
        var slug = NormalizeSlug(input.Slug);
        if (companyName.Length is < 1 or > 160) throw new ArgumentException("Company name is required.");
        if (await dbContext.Tenants.AnyAsync(value => value.Slug == slug, cancellationToken))
            throw new InvalidOperationException("This tenant slug is unavailable.");

        ErpBundleVersion? bundle = null;
        if (input.BundleRunId is { } runId)
        {
            bundle = await dbContext.ErpBundleVersions
                .Include(value => value.GenerationRun).ThenInclude(value => value.GenerationProject)
                .Where(value => value.GenerationRunId == runId && value.GenerationRun.GenerationProject.CustomerId == ownerId)
                .OrderByDescending(value => value.Version)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Generated ERP bundle was not found.");
        }

        var tenant = new Tenant
        {
            OwnerId = ownerId,
            StableId = Guid.NewGuid().ToString("N"),
            Slug = slug,
            CompanyName = companyName,
            Plan = string.IsNullOrWhiteSpace(input.Plan) ? "starter" : input.Plan.Trim().ToLowerInvariant()
        };
        var publicSuffix = runtimeOptions.Value.StagingPublicHostSuffix.TrimStart('.');
        var publicAuthority = $"{slug}.{publicSuffix}";
        var host = publicAuthority.Split(':')[0].ToLowerInvariant();
        var databaseName = TenantDatabaseNamePolicy.BuildExpected(tenant.StableId, "staging");
        tenant.Environments.Add(new TenantEnvironment
        {
            Environment = "staging",
            Host = host,
            DatabaseName = databaseName,
            CredentialSecretReference = $"tenant-db:{tenant.StableId}:staging:{databaseName}",
            RuntimeUrl = $"{runtimeOptions.Value.Scheme}://{publicAuthority}",
            Status = bundle is null ? "pending_bundle" : "pending_deployment"
        });
        dbContext.Tenants.Add(tenant);
        if (bundle is not null)
        {
            bundle.GenerationRun.GenerationProject.Tenant = tenant;
            dbContext.Deployments.Add(new Deployment
            {
                Tenant = tenant,
                ErpBundleVersion = bundle,
                Environment = "staging",
                Status = "pending",
                OperationId = $"staging:{tenant.StableId}:{bundle.ContentHash}"
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreateTenantViewModel(
            Map(tenant),
            new TenantDeploymentViewModel(bundle is null ? "pending_bundle" : "pending", "ERP environment created."));
    }

    public TenantViewModel Map(Tenant tenant)
    {
        var environment = tenant.Environments.OrderBy(value => value.Environment).FirstOrDefault();
        return new TenantViewModel(
            tenant.Id,
            tenant.StableId,
            tenant.Slug,
            tenant.CompanyName,
            tenant.Plan,
            tenant.Status,
            tenant.SetupStatus,
            environment?.Environment,
            environment?.Host,
            environment?.RuntimeUrl ?? (environment is null ? null : $"{runtimeOptions.Value.Scheme}://{environment.Host}"),
            environment?.Status,
            tenant.CreatedAt,
            tenant.UpdatedAt);
    }

    private static string NormalizeSlug(string? value)
    {
        var slug = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (slug.Length is < 2 or > 63 || slug[0] == '-' || slug[^1] == '-' ||
            slug.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Tenant slug must contain only lowercase letters, numbers, and hyphens.");
        return slug;
    }
}
