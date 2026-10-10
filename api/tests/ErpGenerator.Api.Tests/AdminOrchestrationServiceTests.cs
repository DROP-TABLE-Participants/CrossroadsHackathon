using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Services.Implementations.Admin;

namespace ErpGenerator.Api.Tests;

public sealed class AdminOrchestrationServiceTests
{
    [Fact]
    public async Task Overview_separates_ready_queue_from_scheduled_retries()
    {
        await using var context = Context();
        var ready = Seed(context, "queued", "retryable", "Ready project");
        ready.NextAttemptAt = null;
        var scheduled = Seed(context, "queued", "retryable", "Scheduled project");
        scheduled.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);

        var overview = await service.GetOverviewAsync(CancellationToken.None);

        Assert.Equal(1, overview.QueuedRunCount);
        Assert.Equal(1, overview.RetryScheduledRunCount);
        Assert.NotNull(overview.OldestQueuedAt);
    }

    [Fact]
    public async Task Overview_and_details_expose_safe_durable_orchestration_state()
    {
        await using var context = Context();
        var run = Seed(context, "failed", "retryable");
        run.ModelCalls.Add(new ModelCallRecord
        {
            Phase = "erp_authoring",
            RequestHash = new string('a', 64),
            Status = "completed",
            ProviderResponseId = "resp_safe_identifier",
            ResultJson = """{"secretCustomerEvidence":"must not cross the admin contract"}"""
        });
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);

        var overview = await service.GetOverviewAsync(CancellationToken.None);
        var details = await service.GetAsync(run.Id, CancellationToken.None);

        Assert.Equal(1, overview.FailedLast24HoursCount);
        Assert.NotNull(details);
        Assert.True(details.CanRetryNow);
        Assert.Equal("retryable", details.FailureDisposition);
        var modelCall = Assert.Single(details.ModelCalls);
        Assert.Equal("resp_safe_identifier", modelCall.ProviderResponseId);
        Assert.DoesNotContain(
            details.GetType().GetProperties(),
            property => property.Name.Contains("ResultJson", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Retry_now_requeues_the_same_run_and_writes_event_and_audit()
    {
        await using var context = Context();
        var run = Seed(context, "failed", "retryable");
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);
        var actorId = Guid.NewGuid();

        var action = await service.RetryNowAsync(
            actorId,
            "admin@example.test",
            run.Id,
            "Retry after dependency recovery.",
            CancellationToken.None);

        context.ChangeTracker.Clear();
        var updated = await context.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync();
        Assert.Equal(run.Id, action.RunId);
        Assert.Equal("queued", updated.Status);
        Assert.NotNull(updated.NextAttemptAt);
        Assert.Null(updated.CompletedAt);
        Assert.Contains(updated.Events, value => value.EventType == "admin_retry_requested");
        var audit = await context.AdminAuditRecords.SingleAsync();
        Assert.Equal(actorId, audit.ActorAdminAccountId);
        Assert.Equal("generation_run_retry_requested", audit.Action);
        Assert.Equal(run.Id.ToString(), audit.ResourceId);
    }

    [Fact]
    public async Task Configuration_failure_can_be_retried_after_admin_repairs_configuration()
    {
        await using var context = Context();
        var run = Seed(context, "failed", "configuration");
        run.FailureCode = "generation_configuration_required";
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);

        var details = await service.GetAsync(run.Id, CancellationToken.None);
        Assert.NotNull(details);
        Assert.True(details.CanRetryNow);

        await service.RetryNowAsync(
            Guid.NewGuid(),
            "admin@example.test",
            run.Id,
            "Claude configuration was restored and verified.",
            CancellationToken.None);

        context.ChangeTracker.Clear();
        var updated = await context.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync();
        Assert.Equal("queued", updated.Status);
        Assert.NotNull(updated.NextAttemptAt);
        Assert.Contains(
            updated.Events,
            value => value.EventType == "admin_retry_requested");
    }

    [Fact]
    public async Task Retry_now_rejects_terminal_and_leased_runs()
    {
        await using var context = Context();
        var terminal = Seed(context, "failed", "terminal");
        var leased = Seed(context, "failed", "retryable", "Leased project");
        leased.LeaseId = Guid.NewGuid();
        leased.LeaseOwner = "api-worker:identifier";
        leased.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RetryNowAsync(
                Guid.NewGuid(),
                "admin@example.test",
                terminal.Id,
                "Invalid terminal retry.",
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RetryNowAsync(
                Guid.NewGuid(),
                "admin@example.test",
                leased.Id,
                "Invalid leased retry.",
                CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_running_run_is_durable_and_recovery_finalizes_it()
    {
        await using var context = Context();
        var run = Seed(context, "running", "retryable");
        run.LeaseId = Guid.NewGuid();
        run.LeaseOwner = "worker-a";
        run.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);
        var actorId = Guid.NewGuid();

        var action = await service.CancelAsync(
            actorId,
            "admin@example.test",
            run.Id,
            "Customer requested the generation be stopped.",
            CancellationToken.None);

        Assert.Equal("cancellation_requested", action.Status);
        context.ChangeTracker.Clear();
        var requested = await context.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync();
        Assert.Equal("cancellation_requested", requested.Status);
        Assert.Null(requested.LeaseId);
        Assert.NotNull(requested.CancellationRequestedAt);
        Assert.Contains(
            requested.Events,
            value => value.EventType == "admin_cancellation_requested");

        var leases = new GenerationLeaseService(
            context,
            Options.Create(new GenerationOrchestrationOptions()));
        Assert.Equal(
            1,
            await leases.RecoverExpiredAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        context.ChangeTracker.Clear();
        var cancelled = await context.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync();
        Assert.Equal("cancelled", cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Contains(cancelled.Events, value => value.EventType == "cancelled");
        Assert.Equal(
            "generation_run_cancellation_requested",
            (await context.AdminAuditRecords.SingleAsync()).Action);
    }

    [Fact]
    public async Task Resume_cancelled_run_queues_same_run_and_preserves_history()
    {
        await using var context = Context();
        var run = Seed(context, "cancelled", "retryable");
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.CancelledAt = DateTimeOffset.UtcNow;
        run.CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        run.Stages.Single().Status = "cancelled";
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);

        var action = await service.ResumeAsync(
            Guid.NewGuid(),
            "admin@example.test",
            run.Id,
            "The customer supplied approval to continue.",
            CancellationToken.None);

        Assert.Equal(run.Id, action.RunId);
        Assert.Equal("queued", action.Status);
        context.ChangeTracker.Clear();
        var resumed = await context.GenerationRuns
            .Include(value => value.Stages)
            .Include(value => value.Events)
            .SingleAsync();
        Assert.Equal("queued", resumed.Status);
        Assert.Null(resumed.CompletedAt);
        Assert.Null(resumed.CancelledAt);
        Assert.Null(resumed.CancellationRequestedAt);
        Assert.Equal("cancelled", resumed.Stages.Single().Status);
        Assert.Contains(
            resumed.Events,
            value => value.EventType == "admin_resume_requested");
    }

    [Fact]
    public async Task Invalid_control_actions_do_not_write_audit_records()
    {
        await using var context = Context();
        var completed = Seed(context, "completed", "terminal");
        completed.CompletedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();
        var service = new AdminOrchestrationService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(
                Guid.NewGuid(),
                "admin@example.test",
                completed.Id,
                "Invalid cancellation.",
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResumeAsync(
                Guid.NewGuid(),
                "admin@example.test",
                completed.Id,
                "Invalid resume.",
                CancellationToken.None));

        Assert.Empty(await context.AdminAuditRecords.ToArrayAsync());
    }

    private static ErpGeneratorDbContext Context() =>
        new(new DbContextOptionsBuilder<ErpGeneratorDbContext>()
            .UseInMemoryDatabase($"admin-orchestration-{Guid.NewGuid():N}")
            .Options);

    private static GenerationRun Seed(
        ErpGeneratorDbContext context,
        string status,
        string failureDisposition,
        string companyName = "Acme")
    {
        var customer = new CustomerAccount
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            UserName = $"{Guid.NewGuid():N}@example.test",
            Name = "Customer"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CustomerId = customer.Id,
            CompanyName = companyName,
            Goal = "Replace manual work",
            Industry = "Manufacturing",
            CompanyType = "Small business",
            Status = status == "failed" ? "failed" : "generating"
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            GenerationProjectId = project.Id,
            Status = status,
            FailureDisposition = failureDisposition,
            FailureCode = "test_failure",
            FailureMessage = "Safe customer-facing failure.",
            FailureCount = 1,
            ClaimCount = 1,
            CompletedAt = status == "failed" ? DateTimeOffset.UtcNow : null
        };
        run.Stages.Add(new GenerationRunStage
        {
            StageKey = "erp_design",
            Sequence = 20,
            Status = "failed",
            AttemptCount = 1,
            InputHash = new string('b', 64),
            FailureCode = "test_failure",
            FailureMessage = "Safe administrator diagnostic.",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow
        });
        run.EvidenceSnapshots.Add(new GenerationEvidenceSnapshot
        {
            Sequence = 1,
            ContentSha256 = new string('c', 64),
            EvidenceText = "Private evidence",
            ItemCount = 1
        });
        GenerationRunEventFactory.Add(
            context,
            run,
            GenerationRunEventTypes.Failed,
            "Generation stopped safely.");
        project.GenerationRuns.Add(run);
        context.CustomerAccounts.Add(customer);
        context.GenerationProjects.Add(project);
        return run;
    }
}
