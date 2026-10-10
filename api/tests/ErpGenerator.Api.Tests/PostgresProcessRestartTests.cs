using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Api.Services.Implementations.Admin;
using ErpGenerator.Api.WebHost;

namespace ErpGenerator.Api.Tests;

public sealed class PostgresProcessRestartTests
{
    [PostgresFact]
    public async Task Process_restart_reclaims_run_and_reuses_durable_work()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var runId = await database.SeedQueuedRunAsync();
        await using var provider = await FakeClaudeProvider.StartAsync();
        await using var firstProcess = ChildApiProcess.Start(
            database.ConnectionString,
            provider.Endpoint);

        await provider.AuthoringStarted.Task.WaitAsync(TimeSpan.FromSeconds(45));
        await firstProcess.KillAsync();
        provider.ReleaseAuthoring.TrySetResult();

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            await context.GenerationRuns
                .Where(value => value.Id == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    value => value.LeaseExpiresAt,
                    DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await using var secondProcess = ChildApiProcess.Start(
            database.ConnectionString,
            provider.Endpoint);
        await WaitForCompletionAsync(
            database.Services,
            runId,
            secondProcess,
            TimeSpan.FromSeconds(60));

        await using var verification = database.Services.CreateAsyncScope();
        var dbContext = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await dbContext.GenerationRuns
            .AsSplitQuery()
            .Include(value => value.Stages)
            .Include(value => value.Events)
            .Include(value => value.ModelCalls)
            .Include(value => value.BundleVersions)
            .SingleAsync(value => value.Id == runId);
        Assert.Equal(GenerationRunStatuses.Completed, run.Status);
        Assert.Equal(2, run.ClaimCount);
        Assert.Single(run.BundleVersions);
        Assert.Equal(2, run.ModelCalls.Count);
        Assert.All(
            run.ModelCalls,
            value => Assert.Equal("completed", value.Status));
        Assert.Equal(
            1,
            run.Stages.Single(value =>
                value.StageKey == "company_understanding").AttemptCount);
        Assert.Equal(
            2,
            run.Stages.Single(value =>
                value.StageKey == "erp_design").AttemptCount);
        Assert.Contains(
            run.Events,
            value => value.EventType == GenerationRunEventTypes.LeaseRecovered);
        Assert.Contains(
            run.Events,
            value =>
                value.EventType == GenerationRunEventTypes.StageReused &&
                value.StageKey == "company_understanding");

        var authoringKeys = provider.AuthoringRequestIds.ToArray();
        Assert.Equal(2, authoringKeys.Length);
        Assert.Single(authoringKeys.Distinct(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task Cancellation_fences_active_process_and_resume_reuses_durable_work()
    {
        await using var database =
            await PostgresOrchestrationDatabase.CreateAsync();
        var runId = await database.SeedQueuedRunAsync();
        await using var provider = await FakeClaudeProvider.StartAsync();
        await using var firstProcess = ChildApiProcess.Start(
            database.ConnectionString,
            provider.Endpoint);

        await provider.AuthoringStarted.Task.WaitAsync(TimeSpan.FromSeconds(45));
        await using (var scope = database.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            var administration = new AdminOrchestrationService(context);
            var result = await administration.CancelAsync(
                Guid.NewGuid(),
                "orchestration-admin@example.test",
                runId,
                "Stop the active run to prove durable cancellation fencing.",
                CancellationToken.None);
            Assert.Equal(
                GenerationRunStatuses.CancellationRequested,
                result.Status);
        }

        provider.ReleaseAuthoring.TrySetResult();
        await WaitForStatusAsync(
            database.Services,
            runId,
            firstProcess,
            GenerationRunStatuses.Cancelled,
            TimeSpan.FromSeconds(30));
        await firstProcess.KillAsync();

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            var administration = new AdminOrchestrationService(context);
            var result = await administration.ResumeAsync(
                Guid.NewGuid(),
                "orchestration-admin@example.test",
                runId,
                "Resume the same durable run after verifying cancellation.",
                CancellationToken.None);
            Assert.Equal(GenerationRunStatuses.Queued, result.Status);
        }

        await using var secondProcess = ChildApiProcess.Start(
            database.ConnectionString,
            provider.Endpoint);
        await WaitForStatusAsync(
            database.Services,
            runId,
            secondProcess,
            GenerationRunStatuses.Completed,
            TimeSpan.FromSeconds(60));

        await using var verification = database.Services.CreateAsyncScope();
        var dbContext = verification.ServiceProvider
            .GetRequiredService<ErpGeneratorDbContext>();
        var run = await dbContext.GenerationRuns
            .AsSplitQuery()
            .Include(value => value.Stages)
            .Include(value => value.Events)
            .Include(value => value.BundleVersions)
            .SingleAsync(value => value.Id == runId);
        Assert.Equal(GenerationRunStatuses.Completed, run.Status);
        Assert.Equal(2, run.ClaimCount);
        Assert.Single(run.BundleVersions);
        Assert.Equal(
            1,
            run.Stages.Single(value =>
                value.StageKey == "company_understanding").AttemptCount);
        Assert.Equal(
            2,
            run.Stages.Single(value =>
                value.StageKey == "erp_design").AttemptCount);
        Assert.Contains(
            run.Events,
            value =>
                value.EventType ==
                GenerationRunEventTypes.AdminCancellationRequested);
        Assert.Contains(
            run.Events,
            value => value.EventType == GenerationRunEventTypes.Cancelled);
        Assert.Contains(
            run.Events,
            value =>
                value.EventType ==
                GenerationRunEventTypes.AdminResumeRequested);
        Assert.Contains(
            run.Events,
            value =>
                value.EventType == GenerationRunEventTypes.StageReused &&
                value.StageKey == "company_understanding");
    }

    private static async Task WaitForCompletionAsync(
        IServiceProvider services,
        Guid runId,
        ChildApiProcess process,
        TimeSpan timeout)
    {
        await WaitForStatusAsync(
            services,
            runId,
            process,
            GenerationRunStatuses.Completed,
            timeout);
    }

    private static async Task WaitForStatusAsync(
        IServiceProvider services,
        Guid runId,
        ChildApiProcess process,
        string expectedStatus,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The restarted API exited before completion.\n{process.Output}");
            }
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider
                .GetRequiredService<ErpGeneratorDbContext>();
            var status = await context.GenerationRuns
                .AsNoTracking()
                .Where(value => value.Id == runId)
                .Select(value => value.Status)
                .SingleAsync();
            if (status == expectedStatus) return;
            if (status == GenerationRunStatuses.Failed)
            {
                throw new InvalidOperationException(
                    $"The run failed while waiting for '{expectedStatus}'.\n{process.Output}");
            }
            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"The run did not reach '{expectedStatus}'.\n{process.Output}");
    }

    private sealed class ChildApiProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StringBuilder output = new();
        private readonly Task stdout;
        private readonly Task stderr;

        private ChildApiProcess(Process process)
        {
            this.process = process;
            stdout = DrainAsync(process.StandardOutput);
            stderr = DrainAsync(process.StandardError);
        }

        public bool HasExited => process.HasExited;
        public string Output => output.ToString();

        public static ChildApiProcess Start(
            string connectionString,
            Uri providerEndpoint)
        {
            var assembly = typeof(GenerationWorker).Assembly.Location;
            var start = new ProcessStartInfo("dotnet", $"\"{assembly}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            start.Environment["ConnectionStrings__ControlPlane"] = connectionString;
            start.Environment["Jwt__SigningKey"] =
                "customer-test-signing-key-with-at-least-32-bytes";
            start.Environment["AdminJwt__SigningKey"] =
                "administrator-test-signing-key-with-at-least-32-bytes";
            start.Environment["PortalGateway__Secret"] =
                "portal-test-gateway-secret-with-at-least-32-bytes";
            start.Environment["Claude__Provider"] = "Claude";
            start.Environment["Claude__Model"] = "deterministic-test-model";
            start.Environment["Claude__ApiKey"] = "deterministic-test-key";
            start.Environment["Claude__Endpoint"] = providerEndpoint.ToString();
            start.Environment["Claude__TimeoutSeconds"] = "120";
            start.Environment["GenerationOrchestration__LeaseDurationSeconds"] = "30";
            start.Environment["GenerationOrchestration__HeartbeatIntervalSeconds"] = "5";
            start.Environment["GenerationOrchestration__WorkerIdleDelayMilliseconds"] = "100";
            start.Environment["GenerationOrchestration__WorkerFailureDelayMilliseconds"] = "100";
            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the API process.");
            return new ChildApiProcess(process);
        }

        public async Task KillAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(stdout, stderr);
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            process.Dispose();
        }

        private async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }
    }

    private sealed class FakeClaudeProvider : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource stopping = new();
        private readonly Task loop;
        private int authoringRequests;

        private FakeClaudeProvider(HttpListener listener, Uri endpoint)
        {
            this.listener = listener;
            Endpoint = endpoint;
            loop = ListenAsync();
        }

        public Uri Endpoint { get; }
        public TaskCompletionSource AuthoringStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAuthoring { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> AuthoringRequestIds { get; } = new();

        public static Task<FakeClaudeProvider> StartAsync()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(baseUri.ToString());
            listener.Start();
            return Task.FromResult(new FakeClaudeProvider(
                listener,
                new Uri(baseUri, "v1/messages")));
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            listener.Stop();
            try
            {
                await loop;
            }
            catch (HttpListenerException) when (stopping.IsCancellationRequested)
            {
            }
            listener.Close();
            stopping.Dispose();
        }

        private async Task ListenAsync()
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync()
                        .WaitAsync(stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (HttpListenerException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                _ = HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                using var reader = new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding);
                var requestBody = await reader.ReadToEndAsync();
                var authoring = requestBody.Contains(
                    "generated_erp_bundle",
                    StringComparison.Ordinal);
                if (authoring)
                {
                    var key = context.Request.Headers["x-client-request-id"]
                        ?? throw new InvalidOperationException(
                            "The model call did not carry an client request ID.");
                    AuthoringRequestIds.Enqueue(key);
                    if (Interlocked.Increment(ref authoringRequests) == 1)
                    {
                        AuthoringStarted.TrySetResult();
                        await ReleaseAuthoring.Task;
                    }
                }

                var result = authoring ? ValidBundle() : SufficientAssessment();
                var response = JsonSerializer.Serialize(new
                {
                    id = authoring
                        ? "response-durable-authoring"
                        : "response-durable-assessment",
                    type = "message",
                    role = "assistant",
                    stop_reason = "end_turn",
                    content = new[] { new { type = "text", text = result } },
                    usage = new { input_tokens = 10, output_tokens = 20 }
                });
                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception) when (
                stopping.IsCancellationRequested ||
                !context.Response.OutputStream.CanWrite)
            {
            }
            finally
            {
                context.Response.Close();
            }
        }

        private static string SufficientAssessment() =>
            """
            {"sufficient":true,"summary":"The evidence supports a bounded ERP.","missingInformation":[]}
            """;

        private static string ValidBundle() =>
            """
            {
              "name": "Restart-safe ERP",
              "summary": "A deterministic restart-safe ERP.",
              "modules": ["sales"],
              "entities": [
                {
                  "key": "sales_order",
                  "label": "Sales orders",
                  "fields": [
                    {
                      "key":"number",
                      "label":"Number",
                      "type":"text",
                      "required":true,
                      "references":null
                    }
                  ]
                }
              ],
              "workflows": [],
              "reports": [],
              "dashboards": [],
              "permissions": [
                {"role":"owner","grants":["read:sales_order"]}
              ],
              "acceptanceTests": [
                {
                  "key":"create_sales_order",
                  "label":"Create a sales order",
                  "kind":"entity_create",
                  "entityKey":"sales_order",
                  "reportKey":null,
                  "values":[{"fieldKey":"number","value":"SO-1"}]
                }
              ],
              "assumptions": [],
              "openIssues": []
            }
            """;
    }
}
