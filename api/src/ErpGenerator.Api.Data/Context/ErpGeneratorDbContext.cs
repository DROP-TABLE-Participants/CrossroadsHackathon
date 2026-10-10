using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using ErpGenerator.Api.Data.Entities.Common;
using ErpGenerator.Api.Data.Entities.Admin;
using ErpGenerator.Api.Data.Entities.Demo;
using ErpGenerator.Api.Data.Entities.Generation;
using ErpGenerator.Api.Data.Entities.Identity;
using ErpGenerator.Api.Data.Entities.Tenants;
using ErpGenerator.Api.Data.Realtime;

namespace ErpGenerator.Api.Data.Context;

public sealed class ErpGeneratorDbContext(
    DbContextOptions<ErpGeneratorDbContext> options,
    IControlPlaneRealtimePublisher? realtimePublisher = null)
    : IdentityDbContext<CustomerAccount, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<CustomerAccount> CustomerAccounts => Users;
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantEnvironment> TenantEnvironments => Set<TenantEnvironment>();
    public DbSet<GenerationProject> GenerationProjects => Set<GenerationProject>();
    public DbSet<GenerationProjectInputDocument> GenerationProjectInputDocuments => Set<GenerationProjectInputDocument>();
    public DbSet<GenerationProjectInputSource> GenerationProjectInputSources => Set<GenerationProjectInputSource>();
    public DbSet<NangoSyncedRecord> NangoSyncedRecords => Set<NangoSyncedRecord>();
    public DbSet<GenerationProjectParticipant> GenerationProjectParticipants => Set<GenerationProjectParticipant>();
    public DbSet<InterviewRequest> InterviewRequests => Set<InterviewRequest>();
    public DbSet<InterviewSession> InterviewSessions => Set<InterviewSession>();
    public DbSet<InterviewMessage> InterviewMessages => Set<InterviewMessage>();
    public DbSet<InterviewAnswer> InterviewAnswers => Set<InterviewAnswer>();
    public DbSet<GenerationRun> GenerationRuns => Set<GenerationRun>();
    public DbSet<GenerationEvidenceSnapshot> GenerationEvidenceSnapshots => Set<GenerationEvidenceSnapshot>();
    public DbSet<GenerationEvidenceSnapshotItem> GenerationEvidenceSnapshotItems => Set<GenerationEvidenceSnapshotItem>();
    public DbSet<GenerationRunStage> GenerationRunStages => Set<GenerationRunStage>();
    public DbSet<GenerationRunEvent> GenerationRunEvents => Set<GenerationRunEvent>();
    public DbSet<PendingWorkItem> PendingWorkItems => Set<PendingWorkItem>();
    public DbSet<ModelCallRecord> ModelCallRecords => Set<ModelCallRecord>();
    public DbSet<GenerationAgentWorkItem> GenerationAgentWorkItems => Set<GenerationAgentWorkItem>();
    public DbSet<GenerationAgentWorkItemDependency> GenerationAgentWorkItemDependencies => Set<GenerationAgentWorkItemDependency>();
    public DbSet<GenerationAgentExecutionAttempt> GenerationAgentExecutionAttempts => Set<GenerationAgentExecutionAttempt>();
    public DbSet<GenerationArtifactVersion> GenerationArtifactVersions => Set<GenerationArtifactVersion>();
    public DbSet<ErpBundleVersion> ErpBundleVersions => Set<ErpBundleVersion>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<TenantDatabaseSecret> TenantDatabaseSecrets => Set<TenantDatabaseSecret>();
    public DbSet<RuntimeDeploymentEvent> RuntimeDeploymentEvents => Set<RuntimeDeploymentEvent>();
    public DbSet<OwnerActivation> OwnerActivations => Set<OwnerActivation>();
    public DbSet<RuntimeFeedback> RuntimeFeedback => Set<RuntimeFeedback>();
    public DbSet<RuntimeUsageEvent> RuntimeUsageEvents => Set<RuntimeUsageEvent>();
    public DbSet<DemoBooking> DemoBookings => Set<DemoBooking>();
    public DbSet<DemoInstance> DemoInstances => Set<DemoInstance>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<AdminAuditRecord> AdminAuditRecords => Set<AdminAuditRecord>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("control_plane");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ErpGeneratorDbContext).Assembly,
            type => type.Namespace?.Contains(".Configurations.AdminIdentity", StringComparison.Ordinal) != true);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        var realtimeChanges = CaptureRealtimeChanges();
        var result = await base.SaveChangesAsync(cancellationToken);
        if (realtimePublisher is not null && realtimeChanges.Count > 0)
        {
            await realtimePublisher.PublishAsync(this, realtimeChanges, cancellationToken);
        }

        return result;
    }

    private IReadOnlyCollection<ControlPlaneProjectChange> CaptureRealtimeChanges()
    {
        var projects = new Dictionary<Guid, ControlPlaneProjectChange>();

        void Add(Guid projectId, Guid? customerId = null, bool removed = false)
        {
            if (projectId == Guid.Empty) return;
            if (projects.TryGetValue(projectId, out var current))
            {
                projects[projectId] = current with
                {
                    CustomerId = current.CustomerId ?? customerId,
                    Removed = current.Removed || removed
                };
                return;
            }

            projects[projectId] = new ControlPlaneProjectChange(projectId, customerId, removed);
        }

        foreach (var entry in ChangeTracker.Entries()
                     .Where(value => value.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            switch (entry.Entity)
            {
                case GenerationProject project:
                    Add(project.Id, project.CustomerId, entry.State == EntityState.Deleted);
                    break;
                case GenerationProjectInputDocument document:
                    Add(document.GenerationProjectId);
                    break;
                case GenerationProjectInputSource source:
                    Add(source.GenerationProjectId);
                    break;
                case GenerationProjectParticipant participant:
                    Add(participant.GenerationProjectId);
                    break;
                case InterviewRequest request:
                    Add(request.GenerationProjectId);
                    break;
                case GenerationRun run:
                    Add(run.GenerationProjectId);
                    break;
                case GenerationRunStage stage when stage.GenerationRun is not null:
                    Add(stage.GenerationRun.GenerationProjectId);
                    break;
                case GenerationRunEvent runEvent when runEvent.GenerationRun is not null:
                    Add(runEvent.GenerationRun.GenerationProjectId);
                    break;
                case PendingWorkItem item when item.GenerationRun is not null:
                    Add(item.GenerationRun.GenerationProjectId);
                    break;
                case ModelCallRecord call when call.GenerationRun is not null:
                    Add(call.GenerationRun.GenerationProjectId);
                    break;
                case GenerationAgentWorkItem workItem when workItem.GenerationRun is not null:
                    Add(workItem.GenerationRun.GenerationProjectId);
                    break;
                case GenerationAgentExecutionAttempt attempt
                    when attempt.GenerationAgentWorkItem?.GenerationRun is not null:
                    Add(attempt.GenerationAgentWorkItem.GenerationRun.GenerationProjectId);
                    break;
                case GenerationArtifactVersion artifact when artifact.GenerationRun is not null:
                    Add(artifact.GenerationRun.GenerationProjectId);
                    break;
                case ErpBundleVersion bundle when bundle.GenerationRun is not null:
                    Add(bundle.GenerationRun.GenerationProjectId);
                    break;
                case Deployment deployment when deployment.ErpBundleVersion?.GenerationRun is not null:
                    Add(deployment.ErpBundleVersion.GenerationRun.GenerationProjectId);
                    break;
                case NangoSyncedRecord record when record.GenerationProjectInputSource is not null:
                    Add(record.GenerationProjectInputSource.GenerationProjectId);
                    break;
                case InterviewAnswer answer when answer.InterviewRequest is not null:
                    Add(answer.InterviewRequest.GenerationProjectId);
                    break;
                case InterviewSession session when session.InterviewRequest is not null:
                    Add(session.InterviewRequest.GenerationProjectId);
                    break;
                case InterviewMessage message
                    when message.InterviewSession?.InterviewRequest is not null:
                    Add(message.InterviewSession.InterviewRequest.GenerationProjectId);
                    break;
            }
        }

        return projects.Values.ToArray();
    }
}
