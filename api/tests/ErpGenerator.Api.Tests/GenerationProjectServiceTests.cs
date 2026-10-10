using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Services.Implementations.Admin;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Tests;

public sealed class GenerationProjectServiceTests
{
    [Fact]
    public async Task A_guest_cannot_queue_another_project_while_a_run_is_resuming()
    {
        await using var db = CreateContext();
        var customer = Customer();
        customer.IsGuest = true;
        db.CustomerAccounts.Add(customer);
        await db.SaveChangesAsync();
        var service = new GenerationProjectService(db, new FakeInputSources(), new FakeEvidence());
        var first = await service.CreateAsync(customer.Id, new("First ERP", "Manage manufacturing", "hybrid", Industry: "Manufacturing", CompanyType: "Manufacturer"), CancellationToken.None);
        var second = await service.CreateAsync(customer.Id, new("Second ERP", "Manage manufacturing", "hybrid", Industry: "Manufacturing", CompanyType: "Manufacturer"), CancellationToken.None);
        db.GenerationRuns.Add(new() { GenerationProjectId = first.Id, Status = "resuming" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(customer.Id, second.Id, false, null, CancellationToken.None));
        Assert.Single(await db.GenerationRuns.ToListAsync());
    }

    [Theory]
    [InlineData("connector_first", true, false)]
    [InlineData("interview_first", false, true)]
    [InlineData("hybrid", true, true)]
    public async Task Initial_description_and_interview_answers_are_durable_evidence(string mode, bool description, bool interview)
    {
        await using var db = CreateContext();
        var customer = Customer();
        db.CustomerAccounts.Add(customer);
        await db.SaveChangesAsync();
        var service = new GenerationProjectService(db, new FakeInputSources(), null!);
        var project = await service.CreateAsync(customer.Id, new("Chocolate maker", "Track orders and production", mode, Industry: "Manufacturing", CompanyType: "Manufacturer", Description: description ? "Make single-origin chocolate bars." : null, InterviewAnswers: interview ? [new("How are orders fulfilled?", "Produce batches and release quality before shipment.")] : null), CancellationToken.None);
        var evidence = await db.GenerationProjectInputDocuments.Where(v => v.GenerationProjectId == project.Id).ToListAsync();
        Assert.Equal((description ? 1 : 0) + (interview ? 1 : 0), evidence.Count);
        Assert.All(evidence, v => Assert.Equal(64, v.ContentSha256.Length));
    }
    [Fact]
    public async Task Create_accepts_and_normalizes_a_multiline_project_goal()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        dbContext.CustomerAccounts.Add(customer);
        await dbContext.SaveChangesAsync();
        var service = new GenerationProjectService(dbContext, new FakeInputSources(), null!);

        var result = await service.CreateAsync(
            customer.Id,
            new CreateGenerationProjectInputModel(
                "Dandelion Chocolate",
                "Where we lose time:\r\n- Chasing order status\r- Inventory spreadsheets",
                "connector_first",
                Industry: "Food & beverage",
                CompanyType: "Partner-owned, 11–50 people, single site"),
            CancellationToken.None);

        Assert.Equal(
            "Where we lose time:\n- Chasing order status\n- Inventory spreadsheets",
            result.Goal);
        Assert.Equal(result.Goal, (await dbContext.GenerationProjects.SingleAsync()).Goal);
    }

    [Fact]
    public async Task Create_rejects_unsupported_control_characters_in_a_project_goal()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        dbContext.CustomerAccounts.Add(customer);
        await dbContext.SaveChangesAsync();
        var service = new GenerationProjectService(dbContext, new FakeInputSources(), null!);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            customer.Id,
            new CreateGenerationProjectInputModel(
                "Dandelion Chocolate",
                "Replace paper\u0000processes",
                "connector_first",
                Industry: "Food & beverage",
                CompanyType: "Partner-owned"),
            CancellationToken.None));

        Assert.Equal("Project goal contains unsupported control characters.", exception.Message);
    }

    [Fact]
    public async Task Start_waits_for_explicit_connector_scope_instead_of_ignoring_the_source()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        var project = Project(customer);
        project.InputSources.Add(new GenerationProjectInputSource
        {
            ProviderConfigKey = "notion",
            ConnectionId = "notion-connection",
            SourceKind = "Notion",
            Status = "connected",
            ScopeMode = "unconfigured"
        });
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        var evidence = new FakeEvidence();
        var service = new GenerationProjectService(dbContext, new FakeInputSources(), evidence);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(
                customer.Id,
                project.Id,
                false,
                new StartGenerationInputModel(),
                CancellationToken.None));

        Assert.Contains("Choose the company data", exception.Message);
        Assert.Empty(await dbContext.GenerationRuns.ToListAsync());
        Assert.False(evidence.Called);
    }

    [Fact]
    public async Task Start_freezes_already_imported_connector_evidence_without_fetching_nango_again()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        var project = Project(customer);
        project.InputSources.Add(new GenerationProjectInputSource
        {
            ProviderConfigKey = "notion",
            ConnectionId = "notion-connection",
            SourceKind = "Notion",
            Status = "ready",
            ScopeMode = "selected_roots",
            SelectedExternalIdsJson = """["page-1"]""",
            RetainContentUntil = DateTimeOffset.UtcNow.AddDays(30)
        });
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        var evidence = new FakeEvidence();
        var service = new GenerationProjectService(dbContext, new FakeInputSources(), evidence);

        var result = await service.StartAsync(
            customer.Id,
            project.Id,
            false,
            new StartGenerationInputModel(),
            CancellationToken.None);

        Assert.Equal("generating", result.Status);
        Assert.True(evidence.Called);
        Assert.False(evidence.RefreshConnectedSources);
    }

    [Fact]
    public async Task Start_with_assumptions_appends_to_the_existing_run_timeline()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        var project = Project(customer);
        var run = new GenerationRun
        {
            GenerationProject = project,
            Status = "waiting_for_input"
        };
        run.Events.Add(new GenerationRunEvent
        {
            GenerationRun = run,
            Sequence = 1,
            EventType = "waiting_for_input",
            Summary = "Waiting for input."
        });
        project.GenerationRuns.Add(run);
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        var service = new GenerationProjectService(
            dbContext,
            new FakeInputSources(),
            new FakeEvidence());

        await service.StartAsync(
            customer.Id,
            project.Id,
            true,
            new StartGenerationInputModel(),
            CancellationToken.None);

        dbContext.ChangeTracker.Clear();
        var updated = await dbContext.GenerationRuns
            .Include(value => value.Events)
            .SingleAsync();
        Assert.Equal("queued", updated.Status);
        Assert.Equal([1, 2], updated.Events.OrderBy(value => value.Sequence)
            .Select(value => value.Sequence));
        Assert.Contains(
            updated.Events,
            value => value.EventType == "input_received");
    }

    [Fact]
    public async Task Customer_and_administrator_read_the_same_canonical_run_status()
    {
        var statuses = new[]
        {
            "queued",
            "running",
            "waiting_for_input",
            "cancellation_requested",
            "cancelled",
            "completed",
            "failed"
        };

        foreach (var status in statuses)
        {
            await using var dbContext = CreateContext();
            var customer = Customer();
            var project = Project(customer);
            var run = new GenerationRun
            {
                GenerationProject = project,
                GenerationProjectId = project.Id,
                Status = status,
                FailureDisposition = status == "failed" ? "terminal" : null,
                CompletedAt = status is "completed" or "cancelled" or "failed"
                    ? DateTimeOffset.UtcNow
                    : null
            };
            if (status == GenerationRunStatuses.WaitingForInput)
            {
                run.PendingWorkItems.Add(new PendingWorkItem
                {
                    GenerationRun = run,
                    Key = "blocking-input",
                    Kind = "interview_request",
                    Prompt = "Provide blocking information.",
                    Status = "pending",
                    Blocking = true
                });
            }
            project.GenerationRuns.Add(run);
            dbContext.AddRange(customer, project);
            await dbContext.SaveChangesAsync();
            var customerService = new GenerationProjectService(
                dbContext,
                new FakeInputSources(),
                new FakeEvidence());
            var adminService = new AdminOrchestrationService(dbContext);

            var customerView = await customerService.GetAsync(
                customer.Id,
                project.Id,
                CancellationToken.None);
            var adminView = await adminService.GetAsync(
                run.Id,
                CancellationToken.None);

            Assert.NotNull(customerView);
            Assert.NotNull(adminView);
            Assert.Equal(
                status,
                Assert.Single(customerView.GenerationRuns).Status);
            Assert.Equal(status, adminView.Status);
        }
    }

    [Fact]
    public async Task Customer_and_administrator_share_resume_recovery_status()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        var project = Project(customer);
        project.Status = "needs_information";
        var run = new GenerationRun
        {
            GenerationProject = project,
            Status = GenerationRunStatuses.WaitingForInput,
            ResumeFailureCount =
                GenerationRunPresentationStatus
                    .ResumeFailureAttentionThreshold,
            ResumeFailureMessage =
                "Automatic generation resume did not complete. The worker will retry."
        };
        run.PendingWorkItems.Add(new PendingWorkItem
        {
            GenerationRun = run,
            Key = "resolved-input",
            Kind = "interview_request",
            Prompt = "Already answered.",
            Status = "resolved",
            Blocking = true,
            ResolvedAt = DateTimeOffset.UtcNow
        });
        project.GenerationRuns.Add(run);
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        var customerService = new GenerationProjectService(
            dbContext,
            new FakeInputSources(),
            new FakeEvidence());
        var adminService = new AdminOrchestrationService(dbContext);

        var customerView = await customerService.GetAsync(
            customer.Id,
            project.Id,
            CancellationToken.None);
        var adminView = await adminService.GetAsync(
            run.Id,
            CancellationToken.None);

        Assert.NotNull(customerView);
        Assert.NotNull(adminView);
        Assert.Equal("failed", customerView.Status);
        var customerRun = Assert.Single(
            customerView.GenerationRuns);
        Assert.Equal("resume_failed", customerRun.Status);
        Assert.Equal(
            run.ResumeFailureMessage,
            customerRun.LastError);
        Assert.Equal("resume_failed", adminView.Status);
        Assert.Equal(
            "generation_resume_failed",
            adminView.FailureCode);
        Assert.Equal(
            run.ResumeFailureMessage,
            adminView.FailureMessage);
    }

    [Fact]
    public async Task Start_queues_durably_while_a_connector_syncs_and_worker_starts_it_when_ready()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        var project = Project(customer);
        var source = new GenerationProjectInputSource
        {
            ProviderConfigKey = "notion",
            ConnectionId = "notion-connection",
            SourceKind = "Notion",
            ScopeMode = "all_accessible",
            Status = "syncing"
        };
        project.InputSources.Add(source);
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        var evidence = new FakeEvidence();
        var service = new GenerationProjectService(dbContext, new FakeInputSources(), evidence);

        var waiting = await service.StartAsync(
            customer.Id,
            project.Id,
            false,
            new StartGenerationInputModel(),
            CancellationToken.None);

        Assert.Equal("waiting_for_evidence", waiting.Status);
        Assert.False(waiting.CanStartGeneration);
        Assert.Equal("connector_syncing", waiting.GenerationBlockedReason);
        Assert.NotNull(project.GenerationRequestedAt);
        Assert.Empty(await dbContext.GenerationRuns.ToListAsync());
        Assert.False(evidence.Called);

        source.Status = "ready";
        source.RetainContentUntil = DateTimeOffset.UtcNow.AddDays(30);
        await dbContext.SaveChangesAsync();
        var queued = new QueuedGenerationService(
            dbContext,
            evidence,
            new NoopResumeService());

        Assert.True(await queued.TryStartNextReadyAsync(CancellationToken.None));
        Assert.Equal("generating", project.Status);
        Assert.Null(project.GenerationRequestedAt);
        Assert.Equal("queued", (await dbContext.GenerationRuns.SingleAsync()).Status);
        Assert.True(evidence.Called);
        Assert.False(evidence.RefreshConnectedSources);
    }

    [Fact]
    public async Task Start_rejects_a_sync_failure_without_mislabeling_it_as_reauthorization()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        var project = Project(customer);
        project.InputSources.Add(new GenerationProjectInputSource
        {
            ProviderConfigKey = "notion",
            ConnectionId = "notion-connection",
            SourceKind = "Notion",
            ScopeMode = "all_accessible",
            Status = "sync_failed"
        });
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        var service = new GenerationProjectService(dbContext, new FakeInputSources(), new FakeEvidence());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(
                customer.Id,
                project.Id,
                false,
                new StartGenerationInputModel(),
                CancellationToken.None));

        Assert.Contains("could not be synchronized", exception.Message);
        Assert.DoesNotContain("authorized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Queued_generation_is_not_starved_by_more_than_twenty_blocked_projects()
    {
        await using var dbContext = CreateContext();
        var customer = Customer();
        dbContext.CustomerAccounts.Add(customer);
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        for (var index = 0; index < 21; index++)
        {
            var blocked = Project(customer);
            blocked.CompanyName = $"Blocked {index}";
            blocked.GenerationRequestedAt = requestedAt.AddSeconds(index);
            blocked.InputSources.Add(new GenerationProjectInputSource
            {
                ProviderConfigKey = "notion",
                ConnectionId = $"blocked-{index}",
                SourceKind = "Notion",
                ScopeMode = "all_accessible",
                Status = "error"
            });
            dbContext.GenerationProjects.Add(blocked);
        }

        var ready = Project(customer);
        ready.CompanyName = "Ready";
        ready.GenerationRequestedAt = requestedAt.AddHours(1);
        dbContext.GenerationProjects.Add(ready);
        await dbContext.SaveChangesAsync();
        var evidence = new FakeEvidence();
        var queued = new QueuedGenerationService(
            dbContext,
            evidence,
            new NoopResumeService());

        Assert.True(await queued.TryStartNextReadyAsync(CancellationToken.None));
        Assert.Null(ready.GenerationRequestedAt);
        Assert.Equal("generating", ready.Status);
        Assert.Equal(ready.Id, (await dbContext.GenerationRuns.SingleAsync()).GenerationProjectId);
    }

    [Fact]
    public async Task Project_status_uses_the_recorded_runtime_owner_activation()
    {
        await using var dbContext = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var customer = Customer();
        var project = Project(customer);
        project.Status = "generated";
        var run = new GenerationRun
        {
            GenerationProject = project,
            Status = "completed",
            CompletedAt = now
        };
        project.GenerationRuns.Add(run);
        var artifact = TestGenerationArtifactFactory.Create(
            run,
            "{}",
            new string('a', 64));
        var bundle = new ErpBundleVersion
        {
            GenerationRun = run,
            GenerationArtifactVersion = artifact,
            GenerationArtifactVersionId = artifact.Id,
            BundleId = "deployment-verification",
            Version = 1,
            ContentHash = new string('a', 64)
        };
        run.BundleVersions.Add(bundle);
        var tenant = new Tenant
        {
            Owner = customer,
            StableId = "deployment-verification",
            Slug = "deployment-verification",
            CompanyName = project.CompanyName
        };
        var environment = new TenantEnvironment
        {
            Tenant = tenant,
            Environment = "staging",
            Host = "deployment-verification.erpgenerator.test",
            DatabaseName = "erp_tenant_deployment_verification_staging",
            Status = "ready",
            RuntimeUrl = "https://deployment-verification.erpgenerator.test",
            OwnerActivationUrl = "https://deployment-verification.erpgenerator.test/activate/one-time",
            OwnerActivationExpiresAt = now.AddDays(7)
        };
        tenant.Environments.Add(environment);
        environment.OwnerActivations.Add(new OwnerActivation
        {
            TenantEnvironment = environment,
            Email = customer.Email!,
            Status = "activated",
            ActivatedAt = now
        });
        bundle.Deployments.Add(new Deployment
        {
            Tenant = tenant,
            ErpBundleVersion = bundle,
            Environment = "staging",
            Status = "ready",
            OperationId = "deployment-verification",
            RuntimeDeploymentId = "runtime-deployment",
            DeployedBundleVersion = "0.1.0",
            DeployedBundleHash = bundle.ContentHash,
            TestSuiteHash = new string('b', 64),
            TestsStatus = "passed"
        });
        dbContext.AddRange(customer, project, tenant);
        await dbContext.SaveChangesAsync();
        var service = new GenerationProjectService(dbContext, new FakeInputSources(), null!);

        var result = await service.GetAsync(customer.Id, project.Id, CancellationToken.None);

        var activation = Assert.Single(result!.GenerationRuns).Staging!.Activation;
        Assert.NotNull(activation);
        Assert.Equal("activated", activation.Status);
        Assert.Equal(now, activation.ActivatedAt);
        Assert.Equal(customer.Email, activation.ActivationEmail);
    }

    private static ErpGeneratorDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ErpGeneratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static CustomerAccount Customer() => new()
    {
        Email = "owner@example.test",
        NormalizedEmail = "OWNER@EXAMPLE.TEST",
        Name = "Owner",
        PasswordHash = "unused"
    };

    private static GenerationProject Project(CustomerAccount customer) => new()
    {
        Customer = customer,
        CompanyName = "Dandelion Chocolate",
        Industry = "Food & beverage",
        CompanyType = "Partner-owned",
        Goal = "Replace paper and spreadsheets"
    };

    private sealed class FakeEvidence : IGenerationEvidenceService
    {
        public bool Called { get; private set; }
        public bool RefreshConnectedSources { get; private set; }

        public Task CaptureAsync(
            Guid generationRunId,
            bool refreshConnectedSources,
            CancellationToken cancellationToken)
        {
            Called = true;
            RefreshConnectedSources = refreshConnectedSources;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopResumeService : IGenerationResumeService
    {
        public Task<bool> TryResumeReadyAsync(
            Guid generationRunId,
            string trigger,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class FakeInputSources : INangoInputSourceService
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
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task HandleWebhookAsync(
            string body,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeExpiredContentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
