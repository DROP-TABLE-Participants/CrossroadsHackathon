using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class GeneratedStagingDeploymentQueue(
    ErpGeneratorDbContext dbContext,
    IOptions<RuntimeAddressOptions> runtimeOptions) : IGeneratedStagingDeploymentQueue
{
    public async Task<bool> TryQueueNextAsync(CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            }

            var bundle = await dbContext.ErpBundleVersions
                .Include(value => value.Deployments)
                .Include(value => value.GenerationRun)
                    .ThenInclude(value => value.GenerationProject)
                    .ThenInclude(value => value.Tenant)
                    .ThenInclude(value => value!.Environments)
                .Where(value =>
                    value.GenerationRun.Status == "completed" &&
                    value.GenerationRun.GenerationProject.DeployToCustomerStaging &&
                    !dbContext.ErpBundleVersions.Any(candidate =>
                        candidate.GenerationRun.GenerationProjectId ==
                        value.GenerationRun.GenerationProjectId &&
                        (candidate.GenerationRun.CreatedAt > value.GenerationRun.CreatedAt ||
                         candidate.GenerationRunId == value.GenerationRunId &&
                         candidate.Version > value.Version)) &&
                    !value.Deployments.Any(deployment => deployment.Environment == "staging"))
                .OrderBy(value => value.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (bundle is null)
            {
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return false;
            }

            var project = bundle.GenerationRun.GenerationProject;
            var tenant = project.Tenant;
            if (tenant is null)
            {
                var stableId = Guid.NewGuid().ToString("N");
                tenant = new Tenant
                {
                    OwnerId = project.CustomerId,
                    StableId = stableId,
                    Slug = await AvailableSlugAsync(project.CompanyName, project.Id, cancellationToken),
                    CompanyName = project.CompanyName,
                    Plan = "starter"
                };
                project.Tenant = tenant;
                dbContext.Tenants.Add(tenant);
            }

            var staging = tenant.Environments.SingleOrDefault(value => value.Environment == "staging");
            if (staging is null)
            {
                staging = StagingEnvironment(tenant);
                tenant.Environments.Add(staging);
                dbContext.TenantEnvironments.Add(staging);
            }

            var deployment = new Deployment
            {
                Tenant = tenant,
                ErpBundleVersion = bundle,
                Environment = "staging",
                Status = "pending",
                OperationId = $"staging:{tenant.StableId}:{bundle.ContentHash}"
            };
            bundle.Deployments.Add(deployment);
            dbContext.Deployments.Add(deployment);
            staging.Status = "pending_deployment";

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private TenantEnvironment StagingEnvironment(Tenant tenant)
    {
        var settings = runtimeOptions.Value;
        var publicSuffix = settings.StagingPublicHostSuffix.TrimStart('.');
        var publicAuthority = $"{tenant.Slug}.{publicSuffix}";
        var databaseName = TenantDatabaseNamePolicy.BuildExpected(tenant.StableId, "staging");
        return new TenantEnvironment
        {
            Tenant = tenant,
            Environment = "staging",
            Host = publicAuthority.Split(':')[0].ToLowerInvariant(),
            DatabaseName = databaseName,
            CredentialSecretReference =
                $"tenant-db:{tenant.StableId}:staging:{databaseName}",
            RuntimeUrl = $"{settings.Scheme}://{publicAuthority}",
            Status = "pending_deployment"
        };
    }

    private async Task<string> AvailableSlugAsync(
        string companyName,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var baseSlug = NormalizeSlug(companyName);
        if (!await dbContext.Tenants.AnyAsync(value => value.Slug == baseSlug, cancellationToken))
            return baseSlug;

        var suffix = projectId.ToString("N")[..8];
        var shortened = baseSlug[..Math.Min(baseSlug.Length, 63 - suffix.Length - 1)].TrimEnd('-');
        return $"{shortened}-{suffix}";
    }

    private static string NormalizeSlug(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length == 0) slug = "erp";
        return slug[..Math.Min(slug.Length, 63)];
    }
}
