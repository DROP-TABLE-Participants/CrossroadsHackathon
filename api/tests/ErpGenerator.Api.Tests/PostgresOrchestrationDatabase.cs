using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ErpGenerator.Api.Tests;

internal sealed class PostgresOrchestrationDatabase : IAsyncDisposable
{
    private readonly string adminConnectionString;
    private readonly string databaseName;
    private readonly ServiceProvider services;

    private PostgresOrchestrationDatabase(
        string adminConnectionString,
        string databaseName,
        string connectionString,
        ServiceProvider services)
    {
        this.adminConnectionString = adminConnectionString;
        this.databaseName = databaseName;
        ConnectionString = connectionString;
        this.services = services;
    }

    public string ConnectionString { get; }
    public IServiceProvider Services => services;

    public static async Task<PostgresOrchestrationDatabase> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable(
            "ERP_GENERATOR_TEST_POSTGRES")
            ?? throw new InvalidOperationException(
                "ERP_GENERATOR_TEST_POSTGRES is required.");
        var adminBuilder = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false
        };
        var databaseName = $"erpgenerator_orchestration_{Guid.NewGuid():N}";
        await using (var connection =
                     new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = databaseName,
            Pooling = false
        };
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(TimeProvider.System);
        collection.AddDbContext<ErpGeneratorDbContext>(options =>
            options.UseNpgsql(testBuilder.ConnectionString));
        collection.Configure<GenerationOrchestrationOptions>(options =>
        {
            options.LeaseDurationSeconds = 30;
            options.HeartbeatIntervalSeconds = 5;
            options.MaximumAttempts = 3;
            options.InitialRetryDelaySeconds = 1;
            options.MaximumRetryDelaySeconds = 1;
        });
        collection.AddScoped<IGenerationLeaseService, GenerationLeaseService>();
        collection.AddScoped<IGenerationEvidenceService, NoopGenerationEvidenceService>();
        collection.AddScoped<IGenerationResumeService, GenerationResumeService>();
        collection.AddScoped<IQueuedGenerationService, QueuedGenerationService>();
        var services = collection.BuildServiceProvider();

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            await context.Database.MigrateAsync();
        }

        return new PostgresOrchestrationDatabase(
            adminBuilder.ConnectionString,
            databaseName,
            testBuilder.ConnectionString,
            services);
    }

    public async Task<Guid> SeedQueuedRunAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
        var customer = new CustomerAccount
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            UserName = $"{Guid.NewGuid():N}@example.test",
            Name = "PostgreSQL test customer"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CustomerId = customer.Id,
            CompanyName = "PostgreSQL orchestration test",
            Goal = "Prove durable orchestration",
            Industry = "Testing",
            CompanyType = "Test fixture",
            Status = "generating"
        };
        var run = new GenerationRun
        {
            GenerationProject = project,
            GenerationProjectId = project.Id,
            Status = GenerationRunStatuses.Queued
        };
        run.EvidenceSnapshots.Add(new GenerationEvidenceSnapshot
        {
            Sequence = 1,
            EvidenceText = "Deterministic test evidence.",
            ContentSha256 = new string('a', 64),
            ItemCount = 1
        });
        GenerationRunEventFactory.Add(
            context,
            run,
            GenerationRunEventTypes.Queued,
            "PostgreSQL orchestration test queued.");
        project.GenerationRuns.Add(run);
        context.CustomerAccounts.Add(customer);
        context.GenerationProjects.Add(project);
        await context.SaveChangesAsync();
        return run.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText =
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @database AND pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("database", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class NoopGenerationEvidenceService :
        IGenerationEvidenceService
    {
        public Task CaptureAsync(
            Guid generationRunId,
            bool refreshConnectedSources,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
