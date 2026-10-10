using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Tests;

public sealed class PostgresGenerationResumeTests
{
    [PostgresFact]
    public async Task Final_interview_with_existing_events_resumes_once_at_next_sequence()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var seeded = await SeedWaitingInterviewAsync(database.Services);

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            var service = Workflow(
                dbContext,
                scope.ServiceProvider
                    .GetRequiredService<IGenerationResumeService>());
            var invite = await service.CreateInterviewAsync(
                seeded.CustomerId,
                seeded.PendingWorkItemId,
                new CreateInterviewRequestInputModel(
                    "operations@example.test"),
                CancellationToken.None);
            var token = new Uri(invite.InviteLink).Segments.Last();
            await service.StartInterviewAsync(token, CancellationToken.None);
            var answer = await service.AddMessageAsync(
                token,
                new SubmitInterviewMessageInputModel(
                    "Operations approves the final discovery answer.",
                    "postgres-resume-turn-123456789"),
                CancellationToken.None);

            var completed = await service.CompleteTurnAsync(
                token,
                new RequestInterviewAssistantInputModel(
                    answer.Turn.TurnKey),
                CancellationToken.None);
            Assert.True(completed.Completed);

            var replay = await service.CompleteTurnAsync(
                token,
                new RequestInterviewAssistantInputModel(
                    answer.Turn.TurnKey),
                CancellationToken.None);
            Assert.True(replay.Completed);
        }

        await using var verification =
            database.Services.CreateAsyncScope();
        var context = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await context.GenerationRuns
            .AsSplitQuery()
            .Include(value => value.GenerationProject)
            .Include(value => value.Events)
            .Include(value => value.PendingWorkItems)
            .SingleAsync(value => value.Id == seeded.RunId);
        Assert.Equal(GenerationRunStatuses.Queued, run.Status);
        Assert.Equal("generating", run.GenerationProject.Status);
        Assert.Equal(5, run.LastEventSequence);
        Assert.All(run.PendingWorkItems, value =>
            Assert.Equal("resolved", value.Status));
        var inputReceived = Assert.Single(
            run.Events,
            value =>
                value.EventType ==
                GenerationRunEventTypes.InputReceived);
        Assert.Equal(5, inputReceived.Sequence);
    }

    [PostgresFact]
    public async Task Concurrent_resume_attempts_have_one_winner_and_one_event()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var seeded = await SeedReadyRunAsync(database.Services);

        var results = await Task.WhenAll(
            ResumeAsync(database.Services, seeded.RunId, "request-a"),
            ResumeAsync(database.Services, seeded.RunId, "request-b"));
        Assert.Equal(1, results.Count(value => value));

        await using var verification =
            database.Services.CreateAsyncScope();
        var context = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await context.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync(value => value.Id == seeded.RunId);
        Assert.Equal(GenerationRunStatuses.Queued, run.Status);
        Assert.Equal(5, run.LastEventSequence);
        Assert.Single(
            run.Events,
            value =>
                value.EventType ==
                GenerationRunEventTypes.InputReceived);
    }

    [PostgresFact]
    public async Task Worker_reconciliation_resumes_stranded_zero_blocker_run()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var seeded = await SeedReadyRunAsync(database.Services);

        await using (var scope =
                     database.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider
                .GetRequiredService<IQueuedGenerationService>();
            Assert.True(
                await queue.TryResumeNextReadyAsync(
                    CancellationToken.None));
            Assert.False(
                await queue.TryResumeNextReadyAsync(
                    CancellationToken.None));
        }

        await using var verification =
            database.Services.CreateAsyncScope();
        var context = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await context.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync(value => value.Id == seeded.RunId);
        Assert.Equal(GenerationRunStatuses.Queued, run.Status);
        Assert.Single(
            run.Events,
            value =>
                value.EventType ==
                GenerationRunEventTypes.InputReceived);
    }

    [PostgresFact]
    public async Task Saved_interview_survives_resume_failure_and_reconciliation_recovers_it()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var seeded = await SeedWaitingInterviewAsync(database.Services);

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            var failingResume = new GenerationResumeService(
                dbContext,
                new FailingEvidenceService(),
                TimeProvider.System,
                NullLogger<GenerationResumeService>.Instance);
            var service = Workflow(dbContext, failingResume);
            var invite = await service.CreateInterviewAsync(
                seeded.CustomerId,
                seeded.PendingWorkItemId,
                new CreateInterviewRequestInputModel(
                    "operations@example.test"),
                CancellationToken.None);
            var token = new Uri(invite.InviteLink).Segments.Last();
            await service.StartInterviewAsync(token, CancellationToken.None);
            var answer = await service.AddMessageAsync(
                token,
                new SubmitInterviewMessageInputModel(
                    "This answer must survive a resume failure.",
                    "postgres-failed-resume-123456"),
                CancellationToken.None);

            var completed = await service.CompleteTurnAsync(
                token,
                new RequestInterviewAssistantInputModel(
                    answer.Turn.TurnKey),
                CancellationToken.None);
            Assert.True(completed.Completed);
        }

        await using (var failedVerification =
                     database.Services.CreateAsyncScope())
        {
            var context = failedVerification.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            var run = await context.GenerationRuns
                .Include(value => value.PendingWorkItems)
                .SingleAsync(value => value.Id == seeded.RunId);
            Assert.Equal(
                GenerationRunStatuses.WaitingForInput,
                run.Status);
            Assert.Equal(1, run.ResumeFailureCount);
            Assert.All(run.PendingWorkItems, value =>
                Assert.Equal("resolved", value.Status));
            Assert.Single(await context.InterviewAnswers.ToListAsync());
        }

        Assert.True(await ResumeAsync(
            database.Services,
            seeded.RunId,
            "worker_reconciliation"));

        await using var recoveredVerification =
            database.Services.CreateAsyncScope();
        var recoveredContext = recoveredVerification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var recovered = await recoveredContext.GenerationRuns
            .SingleAsync(value => value.Id == seeded.RunId);
        Assert.Equal(GenerationRunStatuses.Queued, recovered.Status);
        Assert.Equal(0, recovered.ResumeFailureCount);
        Assert.Equal(2, recovered.ResumeAttemptCount);
    }

    [PostgresFact]
    public async Task Blocking_work_keeps_run_waiting_without_resume_event()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var seeded = await SeedReadyRunAsync(
            database.Services,
            pendingStatus: "pending");

        Assert.False(await ResumeAsync(
            database.Services,
            seeded.RunId,
            "blocked-request"));

        await using var verification =
            database.Services.CreateAsyncScope();
        var context = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await context.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync(value => value.Id == seeded.RunId);
        Assert.Equal(
            GenerationRunStatuses.WaitingForInput,
            run.Status);
        Assert.DoesNotContain(
            run.Events,
            value =>
                value.EventType ==
                GenerationRunEventTypes.InputReceived);
    }

    private static async Task<SeededWaitingRun>
        SeedWaitingInterviewAsync(IServiceProvider services) =>
        await SeedReadyRunAsync(services, pendingStatus: "pending");

    private static async Task<SeededWaitingRun> SeedReadyRunAsync(
        IServiceProvider services,
        string pendingStatus = "resolved")
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var customer = new CustomerAccount
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            UserName = $"{Guid.NewGuid():N}@example.test",
            Name = "Resume test customer"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CompanyName = "Resume test",
            Goal = "Resume after requested information is supplied",
            Industry = "Testing",
            CompanyType = "Test fixture",
            Status = "needs_information"
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            Status = GenerationRunStatuses.WaitingForInput
        };
        var pending = new PendingWorkItem
        {
            GenerationRun = run,
            Key = "final_interview",
            Kind = "interview_request",
            Prompt = "Provide the final discovery answer.",
            TargetRole = "Operations owner",
            Status = pendingStatus,
            Blocking = true,
            ResolvedAt = pendingStatus == "resolved"
                ? DateTimeOffset.UtcNow
                : null
        };
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.Queued,
            "Queued.");
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.LeaseClaimed,
            "Lease claimed.");
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.StageStarted,
            "Stage started.");
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.WaitingForInput,
            "Waiting for input.");
        dbContext.AddRange(customer, project, run, pending);
        await dbContext.SaveChangesAsync();
        return new SeededWaitingRun(
            customer.Id,
            run.Id,
            pending.Id);
    }

    private static async Task<bool> ResumeAsync(
        IServiceProvider services,
        Guid runId,
        string trigger)
    {
        await using var scope = services.CreateAsyncScope();
        var resume = scope.ServiceProvider
            .GetRequiredService<IGenerationResumeService>();
        return await resume.TryResumeReadyAsync(
            runId,
            trigger,
            CancellationToken.None);
    }

    private static GenerationWorkflowService Workflow(
        ErpGeneratorDbContext dbContext,
        IGenerationResumeService resumeService) =>
        new(
            dbContext,
            Options.Create(new PortalOptions
            {
                PublicBaseUrl = "https://portal.example.test"
            }),
            new NoopInviteDelivery(),
            resumeService,
            NullLogger<GenerationWorkflowService>.Instance);

    private sealed class NoopInviteDelivery :
        IInterviewInviteDeliveryService
    {
        public Task<string> DeliverAsync(
            InterviewInviteDeliveryRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult("not_sent");
    }

    private sealed class FailingEvidenceService :
        IGenerationEvidenceService
    {
        public Task CaptureAsync(
            Guid generationRunId,
            bool refreshConnectedSources,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Injected evidence capture failure.");
    }

    private sealed record SeededWaitingRun(
        Guid CustomerId,
        Guid RunId,
        Guid PendingWorkItemId);
}
