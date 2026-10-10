using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Tests;

public sealed class GenerationEvidenceServiceTests
{
    [Fact]
    public async Task Customer_resolution_notes_are_frozen_into_the_next_evidence_snapshot()
    {
        await using var dbContext = new ErpGeneratorDbContext(
            new DbContextOptionsBuilder<ErpGeneratorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var customer = new CustomerAccount
        {
            Email = "owner@example.test",
            NormalizedEmail = "OWNER@EXAMPLE.TEST",
            UserName = "owner@example.test",
            NormalizedUserName = "OWNER@EXAMPLE.TEST",
            Name = "Owner",
            PasswordHash = "unused"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CustomerId = customer.Id,
            CompanyName = "Acme",
            Industry = "Manufacturing",
            CompanyType = "Furniture maker",
            Goal = "Replace paper processes"
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            GenerationProjectId = project.Id,
            Status = "waiting_for_input"
        };
        run.PendingWorkItems.Add(new PendingWorkItem
        {
            Key = "month_end_surprises",
            Kind = "more_information",
            Prompt = "What causes month-end surprises?",
            Status = "resolved",
            ResolutionNote = "Late supplier bills and unrecorded material usage are the main causes.",
            ResolvedAt = DateTimeOffset.UtcNow
        });
        run.PendingWorkItems.Add(new PendingWorkItem
        {
            Key = "uploaded_documents",
            Kind = "evidence_collection_request",
            Prompt = "Upload current documents.",
            Status = "resolved",
            ResolutionNote = "Evidence document submitted by the customer.",
            ResolvedAt = DateTimeOffset.UtcNow
        });
        project.GenerationRuns.Add(run);
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        var service = new GenerationEvidenceService(dbContext, new NoopNangoInputSourceService());

        await service.CaptureAsync(run.Id, refreshConnectedSources: false, CancellationToken.None);
        var snapshot = await dbContext.GenerationEvidenceSnapshots
            .Include(value => value.Items)
            .SingleAsync(value => value.GenerationRunId == run.Id);

        Assert.Contains("What causes month-end surprises?", snapshot.EvidenceText);
        Assert.Contains("Late supplier bills and unrecorded material usage", snapshot.EvidenceText);
        Assert.DoesNotContain("Evidence document submitted by the customer.", snapshot.EvidenceText);
        Assert.Contains(snapshot.Items, value => value.SourceType == "customer_resolution");
    }

    private sealed class NoopNangoInputSourceService : INangoInputSourceService
    {
        public IReadOnlyList<string> AvailableConnectorKeys => [];

        public Task<NangoConnectSessionViewModel> CreateConnectSessionAsync(
            Guid customerId,
            Guid projectId,
            CreateNangoConnectSessionInputModel input,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReconcileInputSourcesViewModel> ReconcileAsync(
            Guid customerId,
            Guid projectId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConnectorResourceListViewModel> ListResourcesAsync(
            Guid customerId,
            Guid projectId,
            Guid sourceId,
            string? cursor,
            string? query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GenerationInputSourceViewModel> UpdateSelectionAsync(
            Guid customerId,
            Guid projectId,
            Guid sourceId,
            UpdateInputSourceSelectionInputModel input,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GenerationInputSourceViewModel> RefreshAsync(
            Guid customerId,
            Guid projectId,
            Guid sourceId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DisconnectAsync(
            Guid customerId,
            Guid projectId,
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectProjectSourcesAsync(
            Guid customerId,
            Guid projectId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> ImportConnectedEvidenceAsync(
            Guid customerId,
            Guid projectId,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task HandleWebhookAsync(string body, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PurgeExpiredContentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
