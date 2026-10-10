using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class GenerationProjectService(
    ErpGeneratorDbContext dbContext,
    INangoInputSourceService inputSources,
    IGenerationEvidenceService evidence) : IGenerationProjectService
{
    private static readonly HashSet<string> SupportedDiscoveryModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "connector_first",
        "interview_first",
        "hybrid"
    };

    public async Task<GenerationProjectListViewModel> ListAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var projects = await ProjectQuery()
            .Where(value => value.CustomerId == customerId)
            .OrderByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        return new GenerationProjectListViewModel(projects.Select(Map).ToArray());
    }

    public async Task<GenerationProjectViewModel?> GetAsync(
        Guid customerId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await ProjectQuery().SingleOrDefaultAsync(
            value => value.Id == projectId && value.CustomerId == customerId,
            cancellationToken);
        return project is null ? null : Map(project);
    }

    public async Task<GenerationProjectViewModel> CreateAsync(
        Guid customerId,
        CreateGenerationProjectInputModel input,
        CancellationToken cancellationToken)
    {
        var companyName = Required(input.CompanyName, "Company name", 160);
        var goal = RequiredMultiline(input.Goal, "Project goal", 4000);
        var industry = Required(input.Industry, "Industry", 120);
        var companyType = Required(input.CompanyType, "Company type", 120);
        var discoveryMode = input.DiscoveryMode?.Trim().ToLowerInvariant() ?? "connector_first";
        if (!SupportedDiscoveryModes.Contains(discoveryMode)) throw new ArgumentException("Discovery mode is unsupported.");

        var customer = await dbContext.CustomerAccounts.SingleOrDefaultAsync(value => value.Id == customerId, cancellationToken)
            ?? throw new InvalidOperationException("Customer account was not found.");
        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(input.TenantSlug))
        {
            tenantId = await dbContext.Tenants
                .Where(value => value.OwnerId == customerId && value.Slug == input.TenantSlug.Trim().ToLowerInvariant())
                .Select(value => (Guid?)value.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (tenantId is null) throw new InvalidOperationException("ERP environment was not found for this customer.");
        }

        var project = new GenerationProject
        {
            CustomerId = customerId,
            TenantId = tenantId,
            CompanyName = companyName,
            Goal = goal,
            DiscoveryMode = discoveryMode,
            Industry = industry,
            CompanyType = companyType,
            OwnerName = Optional(input.OwnerName, 120) ?? customer.RepresentativeName ?? customer.Name,
            OwnerEmail = Optional(input.OwnerEmail, 320) ?? customer.Email,
            OwnerRoleTitle = Optional(input.OwnerRoleTitle, 120) ?? "CEO / Owner"
        };
        foreach (var contact in input.KnownLeadershipContacts ?? [])
        {
            var name = Optional(contact.Name, 120);
            if (name is null) continue;
            project.Participants.Add(new GenerationProjectParticipant
            {
                Name = name,
                Email = Optional(contact.Email, 320),
                RoleTitle = Optional(contact.RoleTitle, 120),
                Status = "planned"
            });
        }
        dbContext.GenerationProjects.Add(project);
        if (!string.IsNullOrWhiteSpace(input.Description))
            AddInitialEvidence(project, "Company description", RequiredMultiline(input.Description, "Description", 50_000));
        if (input.InterviewAnswers is { Count: > 0 })
        {
            if (input.InterviewAnswers.Count > 10) throw new ArgumentException("Provide up to ten interview answers.");
            foreach (var answer in input.InterviewAnswers)
                AddInitialEvidence(project, Required(answer.Question, "Question", 240), RequiredMultiline(answer.Answer, "Answer", 4000));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(project);
    }

    public async Task<bool> DeleteAsync(Guid customerId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await dbContext.GenerationProjects
            .Include(value => value.GenerationRuns).ThenInclude(value => value.Events)
            .Include(value => value.InputSources)
            .SingleOrDefaultAsync(value => value.Id == projectId && value.CustomerId == customerId, cancellationToken);
        if (project is null) return false;
        if (project.GenerationRuns.Any(value =>
                value.Status is "queued" or "running" or "cancellation_requested"))
            throw new InvalidOperationException("Generation is in progress. Wait for it to finish before deleting this ERP.");
        if (await dbContext.Deployments.AnyAsync(value => value.ErpBundleVersion.GenerationRun.GenerationProjectId == projectId && value.Status == "ready", cancellationToken))
            throw new InvalidOperationException("This ERP has a live environment and cannot be deleted from the customer portal.");
        await inputSources.DisconnectProjectSourcesAsync(customerId, projectId, cancellationToken);
        dbContext.GenerationProjects.Remove(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<GenerationProjectViewModel> StartAsync(
        Guid customerId,
        Guid projectId,
        bool withAssumptions,
        StartGenerationInputModel? input,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsNpgsql()) return await StartCoreAsync(customerId, projectId, withAssumptions, input, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({customerId.ToString()}, 0))", cancellationToken);
        var project = await StartCoreAsync(customerId, projectId, withAssumptions, input, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return project;
    }

    private async Task<GenerationProjectViewModel> StartCoreAsync(Guid customerId, Guid projectId, bool withAssumptions, StartGenerationInputModel? input, CancellationToken cancellationToken)
    {
        var project = await dbContext.GenerationProjects
            .Include(value => value.GenerationRuns)
                .ThenInclude(value => value.Events)
            .Include(value => value.InputSources)
            .SingleOrDefaultAsync(value => value.Id == projectId && value.CustomerId == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Generation project was not found.");

        if (await dbContext.CustomerAccounts.AnyAsync(value => value.Id == customerId && value.IsGuest, cancellationToken) &&
            await dbContext.GenerationRuns.AnyAsync(value => value.GenerationProject.CustomerId == customerId &&
                value.GenerationProjectId != projectId &&
                (value.Status == "queued" || value.Status == "running" || value.Status == "waiting_for_input" ||
                 value.Status == "resuming" || value.Status == "resume_failed" || value.Status == "cancellation_requested"), cancellationToken))
            throw new InvalidOperationException("Finish your current generation before starting another ERP.");

        if (input is not null)
            project.DeployToRuntime = input.DeployToRuntime;

        var active = project.GenerationRuns
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefault(value =>
                value.Status is "queued" or "running" or "waiting_for_input" or "resuming" or "resume_failed" or
                    "cancellation_requested");
        if (active is null)
        {
            EnsureNoUnrecoverableEvidenceBlock(project.InputSources);
            if (ConnectedEvidenceIsStillSynchronizing(project.InputSources))
            {
                project.GenerateWithAssumptions |= withAssumptions;
                project.GenerationRequestedAt ??= DateTimeOffset.UtcNow;
                project.Status = "waiting_for_evidence";
                await dbContext.SaveChangesAsync(cancellationToken);
                return (await GetAsync(customerId, projectId, cancellationToken))!;
            }
            active = new GenerationRun { GenerationProjectId = project.Id, RequestedMode = "default" };
            GenerationRunEventFactory.Add(
                dbContext,
                active,
                GenerationRunEventTypes.Queued,
                "The customer queued ERP generation.");
            project.GenerationRuns.Add(active);
            dbContext.GenerationRuns.Add(active);
        }
        else if (active.Status == "waiting_for_input" && withAssumptions)
        {
            foreach (var item in await dbContext.PendingWorkItems
                         .Where(value => value.GenerationRunId == active.Id && value.Status == "pending")
                         .ToListAsync(cancellationToken))
            {
                item.Status = "waived";
                item.ResolutionNote = "Customer approved generation with explicit assumptions.";
                item.ResolvedAt = DateTimeOffset.UtcNow;
            }
            active.Status = "queued";
            active.FailureCode = null;
            active.FailureMessage = null;
            active.FailureDisposition = null;
            active.NextAttemptAt = null;
            GenerationRunEventFactory.Add(
                dbContext,
                active,
                GenerationRunEventTypes.InputReceived,
                "Pending information was waived because the customer approved explicit assumptions.");
        }

        project.GenerateWithAssumptions |= withAssumptions;
        project.GenerationRequestedAt = null;
        project.Status = "generating";
        await dbContext.SaveChangesAsync(cancellationToken);
        await evidence.CaptureAsync(active.Id, refreshConnectedSources: false, cancellationToken);
        return (await GetAsync(customerId, projectId, cancellationToken))!;
    }

    private static void EnsureNoUnrecoverableEvidenceBlock(
        IEnumerable<GenerationProjectInputSource> sources)
    {
        var reason = ConnectorEvidenceReadiness.BlockingReason(sources);
        if (reason is null or ConnectorEvidenceReadiness.SetupPending or ConnectorEvidenceReadiness.Synchronizing)
            return;

        throw reason switch
        {
            ConnectorEvidenceReadiness.ScopeRequired =>
                new InvalidOperationException(
                    "Choose the company data this connector may use before starting discovery."),
            ConnectorEvidenceReadiness.SyncFailed =>
                new InvalidOperationException(
                    "A connected data source could not be synchronized. Check or remove it before starting discovery."),
            ConnectorEvidenceReadiness.ReconnectRequired =>
                new InvalidOperationException(
                    "A connected data source needs to be authorized again or removed before starting discovery."),
            ConnectorEvidenceReadiness.Expired =>
                new InvalidOperationException(
                    "Connected company data has expired. Refresh or remove it before starting discovery."),
            _ => new InvalidOperationException("Finish connecting company data before starting discovery.")
        };
    }

    private static bool ConnectedEvidenceIsStillSynchronizing(
        IEnumerable<GenerationProjectInputSource> sources) =>
        ConnectorEvidenceReadiness.IsSynchronizing(sources);

    private IQueryable<GenerationProject> ProjectQuery() => dbContext.GenerationProjects.AsNoTracking()
        .AsSplitQuery()
        .Include(value => value.InputSources)
        .Include(value => value.Participants)
        .Include(value => value.InterviewRequests).ThenInclude(value => value.Participant)
        .Include(value => value.GenerationRuns).ThenInclude(value => value.Stages)
        .Include(value => value.GenerationRuns).ThenInclude(value => value.PendingWorkItems).ThenInclude(value => value.InterviewRequest)
        .Include(value => value.GenerationRuns).ThenInclude(value => value.ModelCalls)
        .Include(value => value.GenerationRuns).ThenInclude(value => value.BundleVersions)
            .ThenInclude(value => value.Deployments).ThenInclude(value => value.Tenant).ThenInclude(value => value.Environments)
                .ThenInclude(value => value.OwnerActivations);

    private GenerationProjectViewModel Map(GenerationProject project)
    {
        var generationBlockedReason = ConnectorEvidenceReadiness.BlockingReason(project.InputSources);
        var latestRun = project.GenerationRuns
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefault();
        var latestRunStatus = latestRun is null
            ? null
            : GenerationRunPresentationStatus.For(latestRun);
        var projectStatus = latestRunStatus switch
        {
            GenerationRunPresentationStatus.Resuming => "generating",
            GenerationRunPresentationStatus.ResumeFailed => "failed",
            _ => project.Status
        };
        return new GenerationProjectViewModel(
            project.Id,
            project.TenantId,
            project.CompanyName,
            project.Goal,
            project.DiscoveryMode,
            projectStatus,
            project.Industry,
            project.CompanyType,
            project.OwnerName,
            project.OwnerEmail,
            project.Participants.OrderBy(value => value.CreatedAt).Select(value => new GenerationProjectParticipantViewModel(
                value.Id, value.Name, value.Email, value.RoleTitle, value.Department, value.Status)).ToArray(),
            project.InterviewRequests.OrderBy(value => value.CreatedAt).Select(value => new InterviewRequestViewModel(
                value.Id,
                value.ParticipantId,
                value.Status,
                value.QuestionKey,
                value.ExpiresAt,
                null,
                value.Status == "completed" ? value.UpdatedAt : null,
                new InterviewInviteDeliveryViewModel(
                    value.RecipientEmail is null ? "skipped_no_email" : value.InviteDeliveryStatus,
                    value.RecipientEmail,
                    value.InviteDeliveredAt,
                    value.InviteDeliveryAttemptCount > 0 ? value.UpdatedAt : null,
                    value.InviteDeliveryError,
                    value.InviteDeliveryAttemptCount > 0 ? "smtp" : null,
                    value.InviteDeliveryAttemptCount))).ToArray(),
            project.GenerationRuns.OrderByDescending(value => value.CreatedAt).Select(MapRun).ToArray(),
            project.InputSources.Count,
            project.InputSources.Select(NangoInputSourceService.Map).ToArray(),
            inputSources.AvailableConnectorKeys,
            project.CreatedAt,
            project.GenerationRuns.Count == 0 && generationBlockedReason is null,
            generationBlockedReason);
    }

    private static GenerationRunViewModel MapRun(GenerationRun run)
    {
        var status = GenerationRunPresentationStatus.For(run);
        var generated = run.ModelCalls
            .Where(value =>
                value.Phase.StartsWith("erp_authoring") &&
                value.Status == "completed" &&
                value.ResultJson != null)
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => GeneratedSummary(value.ResultJson!))
            .FirstOrDefault();
        var deployments = run.BundleVersions.SelectMany(value => value.Deployments).ToArray();
        var erp = MapEnvironment(deployments, "erp");
        return new GenerationRunViewModel(
            run.Id,
            status,
            run.RequestedMode,
            run.ActualMode,
            status switch
            {
                "resuming" => "All requested information was received and generation is resuming.",
                "resume_failed" => "Automatic generation resume needs operator attention.",
                "waiting_for_input" => "More information is needed before generation can continue.",
                "cancellation_requested" => "Generation cancellation is being completed safely.",
                "cancelled" => "ERP generation was cancelled.",
                "completed" => "ERP generation completed.",
                "failed" => "Generation stopped after an internal incident.",
                _ => "ERP generation is in progress."
            },
            status switch
            {
                "failed" => run.FailureMessage,
                "resume_failed" => run.ResumeFailureMessage,
                _ => null
            },
            run.HeartbeatAt ?? run.UpdatedAt,
            run.HeartbeatAt,
            run.StartedAt,
            run.CompletedAt,
            erp,
            generated,
            run.Stages.OrderBy(value => value.Sequence).Select(value => new GenerationStageViewModel(
                value.Id, value.StageKey, value.Status, value.Summary)).ToArray(),
            run.ModelCalls.Where(value => value.Status == "completed").OrderBy(value => value.CreatedAt).Select(value => new GenerationArtifactViewModel(
                value.Id,
                value.Phase == "discovery_assessment"
                    ? "company_understanding"
                    : "erp_design_plan",
                "ready",
                value.Phase == "discovery_assessment"
                    ? "Discovery evidence assessed."
                    : "ERP design authored.",
                value.Phase == "discovery_assessment"
                    ? "Company understanding"
                    : "ERP design")).ToArray(),
            run.PendingWorkItems.OrderBy(value => value.CreatedAt).Select(value => new PendingWorkItemViewModel(
                value.Id, value.Kind, value.Status, value.Blocking, value.TargetRole, value.Prompt,
                value.Reason, value.SourceStage, value.InterviewRequest?.Id, value.ResolutionNote,
                value.CreatedAt, value.ResolvedAt)).ToArray());
    }

    private static GenerationEnvironmentViewModel? MapEnvironment(
        IReadOnlyList<Deployment> deployments,
        string environmentName)
    {
        var deployment = deployments.Where(value => value.Environment == environmentName)
            .OrderByDescending(value => value.CreatedAt).FirstOrDefault();
        if (deployment is null) return null;
        var environment = deployment.Tenant.Environments.SingleOrDefault(value => value.Environment == environmentName);
        var ready = deployment.Status == "ready" && deployment.TestsStatus == "passed" &&
                    IsSha256(deployment.TestSuiteHash) && IsSha256(deployment.DeployedBundleHash);
        var ownerActivation = environment?.OwnerActivations
            .OrderByDescending(value => value.UpdatedAt)
            .FirstOrDefault();
        var activationPending = environment?.OwnerActivationExpiresAt > DateTimeOffset.UtcNow;
        var activation = environment?.OwnerActivationUrl is null ? null : new RuntimeActivationSummaryViewModel(
            ownerActivation?.Status ?? (activationPending ? "pending" : "expired"),
            false,
            ownerActivation?.CreatedAt ?? environment.UpdatedAt,
            environment.OwnerActivationExpiresAt,
            ownerActivation?.ActivatedAt,
            environment.UpdatedAt,
            environmentName,
            ownerActivation?.Email);
        return new GenerationEnvironmentViewModel(
            deployment.Status,
            ready ? environment?.RuntimeUrl : null,
            environmentName,
            deployment.RuntimeDeploymentId,
            deployment.DeployedBundleVersion,
            deployment.DeployedBundleHash,
            deployment.Status == "ready" ? deployment.UpdatedAt : null,
            deployment.FailureMessage,
            deployment.TestsStatus,

            activation);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static GeneratedErpSummaryViewModel? GeneratedSummary(string json)
    {
        try
        {
            var bundle = JsonSerializer.Deserialize<GeneratedErpBundle>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return bundle is null ? null : new GeneratedErpSummaryViewModel(
                bundle.Entities.Select(value => value.Label).ToArray(),
                bundle.Workflows.Select(value => value.Label).ToArray(),
                [],
                bundle.Reports.Select(value => value.Label).ToArray(),
                bundle.Dashboards.Select(value => value.Label).ToArray(),
                bundle.Permissions.Select(value => value.Role).ToArray(),
                bundle.OpenIssues);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddInitialEvidence(GenerationProject project, string title, string content)
    {
        project.InputDocuments.Add(new GenerationProjectInputDocument
        {
            Title = title,
            Kind = "initial_evidence",
            Content = content,
            ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant()
        });
    }

    private static string Required(string? value, string label, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is < 1 || text.Length > max)
            throw new ArgumentException($"{label} is required and must be {max} characters or fewer.");
        if (text.Any(char.IsControl))
            throw new ArgumentException($"{label} contains unsupported control characters.");
        return text;
    }

    private static string RequiredMultiline(string? value, string label, int max)
    {
        var text = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (text.Length is < 1 || text.Length > max)
            throw new ArgumentException($"{label} is required and must be {max} characters or fewer.");
        if (text.Any(value => char.IsControl(value) && value is not '\n' and not '\t'))
            throw new ArgumentException($"{label} contains unsupported control characters.");
        return text;
    }

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > max || text.Any(char.IsControl)) throw new ArgumentException("One or more fields exceed allowed length.");
        return text;
    }
}
