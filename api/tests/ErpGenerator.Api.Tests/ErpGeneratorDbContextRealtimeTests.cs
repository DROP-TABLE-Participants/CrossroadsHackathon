using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Data.Context;
using ErpGenerator.Api.Data.Entities.Generation;
using ErpGenerator.Api.Data.Realtime;

namespace ErpGenerator.Api.Tests;

public sealed class ErpGeneratorDbContextRealtimeTests
{
    [Fact]
    public async Task SaveChangesPublishesOneProjectEventForRelatedChanges()
    {
        var publisher = new RecordingRealtimePublisher();
        await using var dbContext = Context(publisher);
        var customerId = Guid.NewGuid();
        var project = new GenerationProject
        {
            CustomerId = customerId,
            CompanyName = "Acme",
            Goal = "Replace spreadsheet workflows.",
            Industry = "Manufacturing",
            CompanyType = "Small business"
        };
        dbContext.GenerationProjects.Add(project);
        project.InputSources.Add(new GenerationProjectInputSource
        {
            GenerationProject = project,
            ProviderConfigKey = "notion",
            ConnectionId = "connection-1",
            SourceKind = "documents"
        });

        await dbContext.SaveChangesAsync();

        var change = Assert.Single(publisher.Changes);
        Assert.Equal(project.Id, change.ProjectId);
        Assert.Equal(customerId, change.CustomerId);
        Assert.False(change.Removed);
    }

    [Fact]
    public async Task DeletingProjectPublishesRemovalWithCustomerIdentity()
    {
        var publisher = new RecordingRealtimePublisher();
        await using var dbContext = Context(publisher);
        var project = new GenerationProject
        {
            CustomerId = Guid.NewGuid(),
            CompanyName = "Acme",
            Goal = "Replace spreadsheet workflows.",
            Industry = "Manufacturing",
            CompanyType = "Small business"
        };
        dbContext.GenerationProjects.Add(project);
        await dbContext.SaveChangesAsync();
        publisher.Changes.Clear();

        dbContext.GenerationProjects.Remove(project);
        await dbContext.SaveChangesAsync();

        var change = Assert.Single(publisher.Changes);
        Assert.Equal(project.Id, change.ProjectId);
        Assert.Equal(project.CustomerId, change.CustomerId);
        Assert.True(change.Removed);
    }

    private static ErpGeneratorDbContext Context(IControlPlaneRealtimePublisher publisher) =>
        new(
            new DbContextOptionsBuilder<ErpGeneratorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            publisher);

    private sealed class RecordingRealtimePublisher : IControlPlaneRealtimePublisher
    {
        public List<ControlPlaneProjectChange> Changes { get; } = [];

        public Task PublishAsync(
            ErpGeneratorDbContext dbContext,
            IReadOnlyCollection<ControlPlaneProjectChange> changes,
            CancellationToken cancellationToken)
        {
            Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }
}
