using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Tests;

public sealed class GenerationWorkflowServiceTests
{
    [Fact]
    public async Task Public_interview_preserves_the_portal_turn_contract_and_resumes_the_run()
    {
        await using var dbContext = CreateContext();
        var customer = new CustomerAccount
        {
            Email = "owner@example.test",
            NormalizedEmail = "owner@example.test",
            Name = "Owner",
            PasswordHash = "unused"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CustomerId = customer.Id,
            CompanyName = "Acme",
            Industry = "Manufacturing",
            Goal = "Replace paper processes",
            Status = "needs_information"
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            GenerationProjectId = project.Id,
            Status = "waiting_for_input"
        };
        var pending = new PendingWorkItem
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            Key = "stock_count_controls",
            Kind = "interview",
            Prompt = "How are stock-count variances approved?",
            TargetRole = "Inventory manager",
            Status = "pending"
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
            "Claimed.");
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.StageStarted,
            "Company understanding started.");
        GenerationRunEventFactory.Add(
            dbContext,
            run,
            GenerationRunEventTypes.WaitingForInput,
            "Waiting for input.");
        dbContext.AddRange(customer, project, run, pending);
        await dbContext.SaveChangesAsync();
        var service = Service(dbContext);

        var invite = await service.CreateInterviewAsync(
            customer.Id,
            pending.Id,
            new CreateInterviewRequestInputModel("inventory@example.test"),
            CancellationToken.None);
        var token = new Uri(invite.InviteLink).Segments.Last();

        var started = await service.StartInterviewAsync(token, CancellationToken.None);
        Assert.Equal("Acme", started.Interview.Project.CompanyName);
        Assert.Equal("How are stock-count variances approved?", started.Interview.PendingWork?.Question);
        Assert.Single(started.Interview.Messages);

        var saved = await service.AddMessageAsync(
            token,
            new SubmitInterviewMessageInputModel(
                "The warehouse manager approves every variance.",
                "interview-submission-123456789"),
            CancellationToken.None);
        Assert.Equal("queued", saved.Turn.Status);
        Assert.Equal("user", saved.Message.Role);

        var replay = await service.AddMessageAsync(
            token,
            new SubmitInterviewMessageInputModel(
                "The warehouse manager approves every variance.",
                "interview-submission-123456789"),
            CancellationToken.None);
        Assert.Equal(saved.Message.Id, replay.Message.Id);

        var assistant = await service.CompleteTurnAsync(
            token,
            new RequestInterviewAssistantInputModel(saved.Turn.TurnKey),
            CancellationToken.None);
        Assert.True(assistant.Completed);
        Assert.Equal("completion", assistant.Message.MessageKind);

        var persistedRun = await dbContext.GenerationRuns.SingleAsync(value => value.Id == run.Id);
        var persistedPending = await dbContext.PendingWorkItems.SingleAsync(value => value.Id == pending.Id);
        Assert.Equal("queued", persistedRun.Status);
        Assert.Equal("resolved", persistedPending.Status);
        Assert.Single(await dbContext.InterviewAnswers.ToListAsync());
        var inputReceived = Assert.Single(
            await dbContext.GenerationRunEvents
                .Where(value =>
                    value.GenerationRunId == run.Id &&
                    value.EventType == GenerationRunEventTypes.InputReceived)
                .ToListAsync());
        Assert.Equal(5, inputReceived.Sequence);

        var replayedAssistant = await service.CompleteTurnAsync(
            token,
            new RequestInterviewAssistantInputModel(saved.Turn.TurnKey),
            CancellationToken.None);
        Assert.True(replayedAssistant.Completed);
        Assert.Single(
            await dbContext.GenerationRunEvents
                .Where(value =>
                    value.GenerationRunId == run.Id &&
                    value.EventType == GenerationRunEventTypes.InputReceived)
                .ToListAsync());
    }

    [Fact]
    public async Task Partial_information_keeps_run_waiting_until_every_blocking_item_is_resolved()
    {
        await using var dbContext = CreateContext();
        var customer = new CustomerAccount
        {
            Email = "owner@example.test",
            NormalizedEmail = "owner@example.test",
            Name = "Owner",
            PasswordHash = "unused"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CustomerId = customer.Id,
            CompanyName = "Acme",
            Industry = "Manufacturing",
            Goal = "Replace paper processes",
            Status = "needs_information"
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            GenerationProjectId = project.Id,
            Status = "waiting_for_input"
        };
        var first = Pending(run, "accounting");
        var second = Pending(run, "inventory");
        dbContext.AddRange(customer, project, run, first, second);
        await dbContext.SaveChangesAsync();
        var service = Service(dbContext);

        var partial = await service.ResolveAsync(
            customer.Id,
            first.Id,
            new ResolvePendingWorkInputModel("Finance supplied the accounting policy."),
            CancellationToken.None);

        Assert.False(partial.AutoResume.Resumed);
        Assert.Equal(
            "waiting_for_input",
            (await dbContext.GenerationRuns.SingleAsync()).Status);

        var completed = await service.ResolveAsync(
            customer.Id,
            second.Id,
            new ResolvePendingWorkInputModel("Warehouse supplied the inventory policy."),
            CancellationToken.None);

        Assert.True(completed.AutoResume.Resumed);
        Assert.Equal("queued", (await dbContext.GenerationRuns.SingleAsync()).Status);
    }

    private static PendingWorkItem Pending(GenerationRun run, string key) =>
        new()
        {
            GenerationRun = run,
            GenerationRunId = run.Id,
            Key = key,
            Kind = "evidence",
            Prompt = $"Provide {key} evidence.",
            TargetRole = "Owner",
            Status = "pending",
            Blocking = true
        };

    private static ErpGeneratorDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ErpGeneratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static GenerationWorkflowService Service(ErpGeneratorDbContext dbContext)
    {
        var resume = new GenerationResumeService(
            dbContext,
            new NoopEvidenceService(),
            TimeProvider.System,
            NullLogger<GenerationResumeService>.Instance);
        return new GenerationWorkflowService(
            dbContext,
            Options.Create(new PortalOptions { PublicBaseUrl = "https://portal.example.test" }),
            new NoopInviteDelivery(),
            resume,
            NullLogger<GenerationWorkflowService>.Instance);
    }

    private sealed class NoopInviteDelivery : IInterviewInviteDeliveryService
    {
        public Task<string> DeliverAsync(InterviewInviteDeliveryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("not_sent");
    }

    private sealed class NoopEvidenceService : IGenerationEvidenceService
    {
        public Task CaptureAsync(
            Guid generationRunId, bool refreshConnectedSources, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
