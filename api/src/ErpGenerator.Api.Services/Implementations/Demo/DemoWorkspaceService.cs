using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Demo;

public sealed class DemoWorkspaceService(ErpGeneratorDbContext db, UserManager<CustomerAccount> users, IOptions<RuntimeAddressOptions> addresses, IRuntimeDeploymentClient runtime, IGenerationProjectService projects) : IDemoWorkspaceService
{
    public const string Version = "dandelion-chocolate.v1";
    private static string TemplatePath => Path.Combine(AppContext.BaseDirectory, "Templates", "dandelion-chocolate", "v1");
    public DemoTemplateViewModel Catalog => new(Version, "Dandelion Chocolate", "A simulated San Francisco chocolate-bar manufacturer. Fulfil a 600-bar wholesale order: 200 available, make 400, release quality, ship 600.", "https://www.dandelionchocolate.com/pages/our-story");

    public async Task<IReadOnlyList<DemoInstanceViewModel>> ListAsync(Guid customerId, CancellationToken ct) =>
        (await db.DemoInstances.AsNoTracking().Include(v => v.Tenant).ThenInclude(v => v.Environments)
            .Where(v => v.CustomerId == customerId && v.Status != "archived").OrderByDescending(v => v.CreatedAt).ToListAsync(ct)).Select(Map).ToArray();

    public async Task<DemoInstanceViewModel> CreateAsync(Guid customerId, Guid? replacesId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await CustomerLockAsync(customerId, ct);
        if (replacesId is { } oldId && !await db.DemoInstances.AnyAsync(v => v.Id == oldId && v.CustomerId == customerId && v.Status == "assigned", ct))
            throw new KeyNotFoundException("Demo was not found in your workspace.");
        var existing = await Query().Where(v => v.CustomerId == customerId && (v.Status == "assigned" || v.Status == "queued" || v.Status == "preparing") &&
            (replacesId == null || v.ReplacesId == replacesId)).OrderByDescending(v => v.CreatedAt).FirstOrDefaultAsync(ct);
        if (existing is not null) return Map(existing);
        var available = (await db.DemoInstances.FromSqlInterpolated($"SELECT * FROM control_plane.demo_instances WHERE \"CustomerId\" IS NULL AND \"Status\" = 'ready' AND \"TemplateVersion\" = {Version} ORDER BY \"CreatedAt\" LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(ct)).FirstOrDefault();
        if (available is null) available = await QueueAsync(customerId, customerId, replacesId, ct);
        else
        {
            await db.Entry(available).Reference(v => v.Tenant).LoadAsync(ct);
            await db.Entry(available.Tenant).Collection(v => v.Environments).LoadAsync(ct);
            available.CustomerId = customerId;
            available.ReplacesId = replacesId;
            available.Tenant.OwnerId = customerId;
            available.Status = "assigned";
            await ArchivePreviousAsync(available, ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Map(available);
    }

    public async Task<GenerationProjectViewModel> CustomizeAsync(Guid customerId, CreateFromTemplateInputModel input, CancellationToken ct)
    {
        if (input.TemplateVersion != Version) throw new ArgumentException("This demo template version is unavailable.");
        var allowed = new Dictionary<string, string>
        {
            ["tasting_notes"] = "Add tasting notes to production batches.",
            ["cocoa_origin"] = "Track cocoa origin and harvest year on ingredient lots.",
            ["quality_approval"] = "Require explicit quality approval before shipment."
        };
        var choices = (input.Choices ?? []).Distinct().ToArray();
        if (choices.Any(v => !allowed.ContainsKey(v))) throw new ArgumentException("An unsupported customization was selected.");
        if (input.Prompt?.Length > 4000) throw new ArgumentException("Use a prompt of 4000 characters or fewer.");
        var knowledge = await File.ReadAllTextAsync(Path.Combine(TemplatePath, "knowledge.md"), ct);
        var description = knowledge + "\n\nRequested customization:\n" + string.Join("\n", choices.Select(v => allowed[v])) + "\n" + input.Prompt;
        return await projects.CreateAsync(customerId, new("Dandelion Chocolate — customized", "Generate a fresh chocolate manufacturing ERP from this knowledge pack and the visitor's selected requirements. Keep explicit questions and assumptions when evidence is incomplete.", "hybrid", Industry: "Chocolate manufacturing", CompanyType: "Manufacturer", Description: description), ct);
    }

    public async Task<RuntimeGuestEntryViewModel> OpenAsync(Guid customerId, Guid? demoId, string? host, Guid? runId, string environment, CancellationToken ct)
    {
        var query = db.TenantEnvironments.Include(v => v.Tenant).Where(v => v.Tenant.OwnerId == customerId && v.Tenant.Status == "active" && v.Status == "ready");
        if (demoId is { } id)
            query = query.Where(v => db.DemoInstances.Any(d => d.Id == id && d.TenantId == v.TenantId && d.CustomerId == customerId && d.Status == "assigned"));
        else if (runId is { } generatedId)
            query = query.Where(v => v.Environment == environment && db.Deployments.Any(d => d.TenantId == v.TenantId && d.Environment == v.Environment && d.Status == "ready" && d.ErpBundleVersion.GenerationRunId == generatedId && d.ErpBundleVersion.GenerationRun.GenerationProject.CustomerId == customerId));
        else if (!string.IsNullOrWhiteSpace(host)) query = query.Where(v => v.Host == host);
        else throw new ArgumentException("Select an ERP from your workspace.");
        var target = await query.SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("This ERP is not available in your workspace.");
        var customer = await db.CustomerAccounts.SingleAsync(v => v.Id == customerId && v.IsActive, ct);
        return await runtime.CreateGuestEntryAsync(new("entry:" + Guid.NewGuid().ToString("N"), Target(target), customerId.ToString(), customer.IsGuest ? "Guest" : customer.Name), ct);
    }

    public async Task PrepareAsync(int count, CancellationToken ct)
    {
        if (count is < 0 or > 20) throw new ArgumentException("Prepare up to twenty presentation copies.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(735218406)", ct);
        var pool = await db.CustomerAccounts.SingleOrDefaultAsync(v => v.Email == "demo-pool@demo.invalid", ct);
        if (pool is null)
        {
            pool = new() { Id = Guid.NewGuid(), Name = "Demo preparation", Email = "demo-pool@demo.invalid", UserName = "demo-pool@demo.invalid", IsActive = false, IsGuest = true };
            var result = await users.CreateAsync(pool);
            if (!result.Succeeded) throw new InvalidOperationException("Demo preparation identity could not be created.");
        }
        var available = await db.DemoInstances.CountAsync(v => v.CustomerId == null && v.TemplateVersion == Version && (v.Status == "queued" || v.Status == "preparing" || v.Status == "ready"), ct);
        foreach (var failed in await Query().Where(v => v.CustomerId == null && v.Status == "failed").ToListAsync(ct))
        {
            failed.Status = "archived";
            failed.Tenant.Status = "archived";
        }
        for (var i = available; i < count; i++) await QueueAsync(pool.Id, null, null, ct);
        // An interrupted preparation is retried against the same immutable target, never reset or reseeded.
        await db.DemoInstances.Where(v => v.Status == "preparing").ExecuteUpdateAsync(v => v.SetProperty(d => d.Status, "queued"), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        DemoInstance? instance;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            instance = (await db.DemoInstances.FromSqlRaw("SELECT * FROM control_plane.demo_instances WHERE \"Status\" = 'queued' ORDER BY \"CreatedAt\" LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(ct)).FirstOrDefault();
            if (instance is null) return false;
            instance.Status = "preparing";
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        await db.Entry(instance).Reference(v => v.Tenant).LoadAsync(ct);
        await db.Entry(instance.Tenant).Collection(v => v.Environments).LoadAsync(ct);
        var target = instance.Tenant.Environments.Single(v => v.Environment == "staging");
        try
        {
            var files = new List<CuratedRuntimeFile>();
            foreach (var path in Directory.EnumerateFiles(TemplatePath, "*", SearchOption.AllDirectories).Order())
            {
                var content = await File.ReadAllTextAsync(path, ct);
                files.Add(new(Path.GetRelativePath(TemplatePath, path).Replace('\\', '/'), content, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()));
            }
            var operationId = "demo:" + instance.Id.ToString("N");
            var requestedTarget = Target(target);
            var receipt = await runtime.DeployCuratedAsync(new(operationId, instance.Id.ToString(), instance.Id.ToString(), requestedTarget, files), ct);
            instance.DeploymentReceiptJson = JsonSerializer.Serialize(receipt);
            if (!receipt.Success || receipt.OperationId != operationId || receipt.Target != requestedTarget ||
                receipt.TestsStatus != "passed" || receipt.TestsBundleHash != receipt.BundleHash || receipt.TestsBundleVersion != receipt.BundleVersion || string.IsNullOrEmpty(receipt.TestSuiteHash))
                throw new InvalidOperationException(receipt.SafeError ?? "The demo did not pass its manufacturing acceptance tests.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (instance.CustomerId is { } customerId) await CustomerLockAsync(customerId, ct);
            target.Status = "ready";
            target.ActiveBundleId = receipt.BundleId;
            target.ActiveBundleHash = receipt.BundleHash;
            target.ActiveBundleVersion = receipt.BundleVersion;
            instance.Status = instance.CustomerId is null ? "ready" : "assigned";
            await ArchivePreviousAsync(instance, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (HttpRequestException)
        {
            // A local startup race or temporary broker outage must leave the work queued.
            instance.Status = "queued";
            await db.SaveChangesAsync(ct);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            instance.Status = "failed";
            instance.FailureMessage = exception is InvalidOperationException ? exception.Message[..Math.Min(500, exception.Message.Length)] : "Preparation failed. Try creating a fresh demo.";
            target.Status = "failed";
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    private IQueryable<DemoInstance> Query() => db.DemoInstances.Include(v => v.Tenant).ThenInclude(v => v.Environments);
    private Task<int> CustomerLockAsync(Guid customerId, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({customerId.ToString()}, 0))", ct);
    private async Task<DemoInstance> QueueAsync(Guid ownerId, Guid? customerId, Guid? replacesId, CancellationToken ct)
    {
        var stableId = Guid.NewGuid().ToString("N");
        var tenant = new Tenant { StableId = stableId, Slug = "demo-" + stableId, CompanyName = "Dandelion Chocolate", OwnerId = ownerId, SetupStatus = "ready" };
        var authority = tenant.Slug + "." + addresses.Value.StagingPublicHostSuffix.TrimStart('.');
        var database = TenantDatabaseNamePolicy.BuildExpected(stableId, "staging");
        tenant.Environments.Add(new() { Environment = "staging", Host = authority.Split(':')[0], DatabaseName = database, CredentialSecretReference = $"tenant-db:{stableId}:staging:{database}", RuntimeUrl = addresses.Value.Scheme + "://" + authority, Status = "pending_deployment" });
        var instance = new DemoInstance { CustomerId = customerId, Tenant = tenant, ReplacesId = replacesId, TemplateVersion = Version };
        db.DemoInstances.Add(instance);
        await db.SaveChangesAsync(ct);
        return instance;
    }
    private async Task ArchivePreviousAsync(DemoInstance replacement, CancellationToken ct)
    {
        if (replacement.ReplacesId is not { } id) return;
        var previous = await Query().SingleAsync(v => v.Id == id && v.CustomerId == replacement.CustomerId, ct);
        previous.Status = "archived";
        previous.Tenant.Status = "archived";
    }
    private static RuntimeDeploymentTargetViewModel Target(TenantEnvironment environment) => new(environment.TenantId.ToString(), environment.Tenant.StableId, environment.Host, environment.Environment, environment.DatabaseName);
    private static DemoInstanceViewModel Map(DemoInstance instance) => new(instance.Id, instance.TemplateVersion, instance.Status, instance.Tenant.CompanyName, instance.Status == "assigned" ? instance.Tenant.Environments.FirstOrDefault()?.RuntimeUrl : null, instance.FailureMessage, instance.CreatedAt);
}
