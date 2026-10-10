using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeBundleAcceptanceIntegrationTests
{
    [PostgresFact]
    public async Task V3_explicit_null_record_field_assertion_emits_null_evidence_digests()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_null_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "acme",
                "acme.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var loader = new ErpBundleYamlLoader();
            var compiled = await RuntimeBundleTestHelper.CompileItServicesV4FolderAsync();
            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                new ErpBundleMigrationPlanner().PlanMigration(null, compiled),
                "staging",
                "staged",
                activate: true,
                "explicit null evidence");
            var seed = (await loader.LoadSeedFromDirectoryAsync(
                RuntimeBundleTestHelper.FindItServicesV4Folder(),
                "demo"))!;
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "explicit_null_field",
                    Label = "An omitted nullable employee role remains null."
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = "demo",
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors =
                [
                    new BundleAcceptanceActor { Id = "admin_actor", Roles = ["admin"] }
                ],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_employee_without_role",
                        Type = "create_record",
                        Entity = "employee",
                        AsRole = "admin",
                        AsActor = "admin_actor",
                        Values = new Dictionary<string, object?>
                        {
                            ["full_name"] = "Null Evidence Employee",
                            ["email"] = $"null-evidence-{suffix}@example.test"
                        },
                        Expected = new BundleAcceptanceExpectedOutcome
                        {
                            Outcome = BundleAcceptanceExpectedOutcomes.Success
                        }
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "employee_role_is_null",
                        AfterStep = "create_employee_without_role",
                        Type = "record_field_equals",
                        Entity = "employee",
                        Record = new BundleRecordSelector { Ref = "create_employee_without_role" },
                        Field = "role",
                        ValueKind = BundleAcceptanceEvidenceValueKinds.Null
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var observation = result.Results.Should().ContainSingle().Which
                .AssertionObservations.Should().ContainSingle().Which;
            var nullDigest = BundleAcceptanceEvidenceDigestContract.Create(
                BundleAcceptanceEvidenceValueKinds.Null,
                null);
            observation.ExpectedValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.Null);
            observation.ActualValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.Null);
            observation.ExpectedValueDigest.Should().Be(nullDigest.Digest);
            observation.ActualValueDigest.Should().Be(nullDigest.Digest);
            observation.ExpectedValue.Should().BeNull();
            observation.ActualValue.Should().BeNull();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Folder_bundle_seed_and_acceptance_tests_pass_on_staging_and_gate_promotion()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var stagingDatabase = $"erp_tenant_accept_stg_{suffix}";
        var productionDatabase = $"erp_tenant_accept_prod_{suffix}";
        var betaDatabase = $"erp_tenant_accept_beta_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, stagingDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, productionDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, betaDatabase);

        try
        {
            var staging = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", stagingDatabase, TenantEnvironmentKind.Staging);
            var production = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", productionDatabase, TenantEnvironmentKind.Production);
            var beta = RuntimeTestTenant.Create("beta", "beta.erpgenerator.example", betaDatabase, TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            var reports = new RuntimeReportStore(factory);
            var seedRunner = new RuntimeSeedRunner(records);
            var testRunner = new RuntimeBundleTestRunner(factory, seedRunner, records, workflows, actions, reports);
            var loader = new ErpBundleYamlLoader();
            var planner = new ErpBundleMigrationPlanner();
            var folder = RuntimeBundleTestHelper.FindItServicesV4Folder();
            var compiled = await RuntimeBundleTestHelper.CompileItServicesV4FolderAsync();
            var plan = planner.PlanMigration(null, compiled);

            await runtimeDatabase.ApplyBundleDeploymentAsync(staging, compiled, plan, "staging", "staged", activate: true, "folder v4 staged");
            await runtimeDatabase.ApplyBundleDeploymentAsync(beta, compiled, plan, "staging", "staged", activate: true, "beta folder v4 staged");

            var seed = await loader.LoadSeedFromDirectoryAsync(folder, "demo");
            seed.Should().NotBeNull();
            var tests = (await loader.LoadAcceptanceTestsFromDirectoryAsync(folder)).ToList();
            tests.Add(new BundleAcceptanceTestDocument
            {
                SourcePath = "inline-dashboard-widget.test.yaml",
                Test = new BundleAcceptanceTest
                {
                    Key = "dashboard_widget_binding",
                    Label = "Dashboard widget metadata is executable"
                },
                Setup = new BundleAcceptanceSetup { Seed = "demo" },
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Type = "dashboard_widget_exists",
                        Widget = "total_projects",
                        Report = "total_projects",
                        Metric = "total_projects"
                    }
                ]
            });
            tests.Should().HaveCount(6);

            var result = await testRunner.RunAsync(staging, compiled.Version, compiled.BundleHash, seed, tests);

            result.Status.Should().Be("passed");
            result.Passed.Should().Be(6);
            result.Failed.Should().Be(0);
            result.BundleVersion.Should().Be(compiled.Version);
            result.BundleHash.Should().Be(compiled.BundleHash);
            result.TestSuiteHash.Should().HaveLength(64);
            result.Results.Select(item => item.Key).Should().Equal(tests.Select(test => test.Test.Key));
            result.Results.Select(item => item.Label).Should().Equal(tests.Select(test => test.Test.Label));
            result.Results.Select(item => item.SuiteIndex).Should().Equal(Enumerable.Range(0, tests.Count));
            result.Results.Should().OnlyContain(item =>
                item.ContractHash.Length == 64 &&
                item.Origin == BundleAcceptanceTestOrigins.Authored);
            (await testRunner.HasPassingResultAsync(staging, compiled.Version, compiled.BundleHash, result.TestSuiteHash)).Should().BeTrue();
            (await testRunner.HasPassingResultAsync(staging, compiled.Version, compiled.BundleHash, new string('0', 64))).Should().BeFalse();
            (await testRunner.HasPassingResultAsync(staging, compiled.Version, "stale-bundle-hash", result.TestSuiteHash)).Should().BeFalse();
            (await testRunner.HasPassingResultAsync(production, compiled.Version, compiled.BundleHash, result.TestSuiteHash)).Should().BeFalse();

            var wrongBundle = () => testRunner.RunAsync(
                staging,
                compiled.Version,
                new string('0', 64),
                seed,
                tests);
            await wrongBundle.Should().ThrowAsync<RuntimeBundleTestException>()
                .WithMessage("*exact active bundle version and hash*");

            var stagingProjects = await records.ListRecordsAsync(staging, "projects", new RuntimeRecordQuery(Search: "Website Redesign"));
            stagingProjects.Total.Should().Be(1);
            var betaProjects = await records.ListRecordsAsync(beta, "projects", new RuntimeRecordQuery(Search: "Website Redesign"));
            betaProjects.Total.Should().Be(0);
            var audit = await records.ListAuditEventsAsync(staging, 200);
            audit.Should().Contain(@event => @event.EventType == "seed.applied");
            audit.Should().Contain(@event => @event.EventType == "bundle_tests.completed");

            var productionPlan = planner.PlanMigration(null, compiled);
            await runtimeDatabase.ApplyBundleDeploymentAsync(production, compiled, productionPlan, "production", "production", activate: true, "folder v4 promoted after tests");
            var productionMetadata = await runtimeDatabase.ReadMetadataAsync(production);
            productionMetadata.BundleVersion.Should().Be("0.4.0");
            productionMetadata.Actions.Should().Contain(action => action.GetProperty("Key").GetString() == "create_invoice_draft");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, stagingDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, productionDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
        }
    }

    [PostgresFact]
    public async Task Acceptance_runner_records_useful_failure_for_bad_assertion()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_fail_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", database, TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var seedRunner = new RuntimeSeedRunner(records);
            var testRunner = new RuntimeBundleTestRunner(
                factory,
                seedRunner,
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var loader = new ErpBundleYamlLoader();
            var compiled = await RuntimeBundleTestHelper.CompileItServicesV4FolderAsync();
            await runtimeDatabase.ApplyBundleDeploymentAsync(tenant, compiled, new ErpBundleMigrationPlanner().PlanMigration(null, compiled), "staging", "staged", activate: true, "folder v4 staged");
            var seed = await loader.LoadSeedFromDirectoryAsync(RuntimeBundleTestHelper.FindItServicesV4Folder(), "demo");
            var failingTest = new BundleAcceptanceTestDocument
            {
                SourcePath = "inline-failing.test.yaml",
                Test = new() { Key = "bad_report", Label = "Bad report assertion" },
                Setup = new() { Seed = "demo" },
                Assertions =
                [
                    new()
                    {
                        Type = "record_count",
                        Entity = "project",
                        Count = 99
                    }
                ]
            };

            var result = await testRunner.RunAsync(tenant, compiled.Version, compiled.BundleHash, seed, [failingTest]);

            result.Status.Should().Be("failed");
            result.Failed.Should().Be(1);
            result.Results.Single().Failures.Should().Contain(failure => failure.Contains("Expected 99 records", StringComparison.OrdinalIgnoreCase));
            (await testRunner.HasPassingResultAsync(tenant, compiled.Version, compiled.BundleHash, result.TestSuiteHash)).Should().BeFalse();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Audit_assertions_ignore_persisted_history_and_support_exact_generated_record_correlation()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_audit_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", database, TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var loader = new ErpBundleYamlLoader();
            var folder = RuntimeBundleTestHelper.FindItServicesV4Folder();
            var compiled = await RuntimeBundleTestHelper.CompileItServicesV4FolderAsync();
            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                new ErpBundleMigrationPlanner().PlanMigration(null, compiled),
                "staging",
                "staged",
                activate: true,
                "folder v4 staged");
            await records.RecordAuditEventAsync(
                tenant,
                user: null,
                "historical.only",
                "This event predates the acceptance run.",
                new Dictionary<string, object?>());

            var stale = new BundleAcceptanceTestDocument
            {
                SourcePath = "00-stale-audit.test.yaml",
                Test = new() { Key = "stale_audit", Label = "Historical audit must not pass" },
                Setup = new() { Seed = "demo" },
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Type = "audit_event_exists",
                        Event = "historical.only"
                    }
                ]
            };
            var fresh = (await loader.LoadAcceptanceTestsFromDirectoryAsync(folder))
                .Single(test => test.Test.Key == "timesheet_approval");
            fresh.SourcePath = "01-fresh-correlated-audit.test.yaml";
            var auditIndex = fresh.Assertions.FindIndex(assertion =>
                assertion.Type.Equals("audit_event_exists", StringComparison.OrdinalIgnoreCase));
            auditIndex.Should().BeGreaterThanOrEqualTo(0);
            fresh.Assertions[auditIndex] = new BundleAcceptanceAssertion
            {
                Type = "audit_event_exists",
                Event = "timesheet.approved",
                Entity = "timesheet",
                Record = new BundleRecordSelector { Ref = "timesheet_submit" }
            };
            var seed = await loader.LoadSeedFromDirectoryAsync(folder, "demo");

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [stale, fresh]);

            result.Status.Should().Be("failed");
            result.Results.Single(item => item.Key == "stale_audit").Should().Match<RuntimeBundleTestResult>(item =>
                item.Status == "failed" &&
                item.Failures.Any(failure => failure.Contains("after the acceptance test boundary", StringComparison.OrdinalIgnoreCase)));
            result.Results.Single(item => item.Key == "timesheet_approval").Status.Should().Be("passed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Acceptance_as_role_owner_cannot_inherit_activated_system_owner_authority()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_owner_rbac_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", database, TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, permissions),
                new RuntimeReportStore(factory));
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var systemOwner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"bootstrap-{suffix}@example.test");
            systemOwner.UserId.Should().BePositive();

            await using (var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString, database)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    update _runtime.role_definitions
                    set grants = '[{"Action":"read","Entity":"customer"}]'::jsonb
                    where id = 'owner'
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var test = new BundleAcceptanceTestDocument
            {
                SourcePath = "inline-owner-rbac.test.yaml",
                Test = new BundleAcceptanceTest { Key = "owner_rbac", Label = "Owner RBAC is real" },
                Setup = new BundleAcceptanceSetup { Seed = "none" },
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_customer",
                        Type = "create_record",
                        Entity = "customer",
                        AsRole = "owner",
                        Values = new Dictionary<string, object?>
                        {
                            ["company_name"] = $"Forbidden Customer {suffix}",
                            ["contact_email"] = $"forbidden-{suffix}@example.test",
                            ["status"] = "active"
                        }
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Type = "record_exists",
                        Entity = "customer",
                        Where = new BundleAcceptanceWhere
                        {
                            Field = "company_name",
                            Op = "eq",
                            Value = $"Forbidden Customer {suffix}"
                        }
                    }
                ]
            };

            var result = await runner.RunAsync(tenant, compiled.Version, compiled.BundleHash, null, [test]);

            result.Status.Should().Be("failed");
            result.Results.Should().ContainSingle().Which.Failures.Should().Contain(failure =>
                failure.Contains("role 'owner'", StringComparison.OrdinalIgnoreCase) &&
                failure.Contains("not allowed to 'create'", StringComparison.OrdinalIgnoreCase));
            (await records.ListRecordsAsync(
                tenant,
                "customer",
                new RuntimeRecordQuery(Search: $"Forbidden Customer {suffix}"))).Total.Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [Fact]
    public void Typed_negative_outcome_integration_fixture_compiles()
    {
        var compile = () => new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());

        compile.Should().NotThrow();
    }

    [PostgresFact]
    public async Task Typed_negative_outcomes_execute_exactly_and_persist_step_observations()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_outcomes_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-outcomes",
                "accept-outcomes.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var tests = CreateExpectedOutcomeTests(suffix);
            BundleAcceptanceTestContractHasher.Stamp(tests);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed: null,
                tests);

            result.Status.Should().Be("passed");
            result.Results.Should().HaveCount(5).And.OnlyContain(test => test.Status == "passed");
            result.Results.Single(test => test.Key == "guard_rollback")
                .StepObservations.Last().Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                    observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.BusinessRuleRejected &&
                    observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.ActionGuard &&
                    observation.ActualTarget == "stop_after_create");
            result.Results.Single(test => test.Key == "not_available")
                .StepObservations.Last().ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable);
            result.Results.Single(test => test.Key == "named_action_denied")
                .StepObservations.Last().ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.PermissionNamedActionExecute);
            result.Results.Single(test => test.Key == "workflow_rejected")
                .StepObservations.Last().Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected &&
                    observation.ActualTarget == "manager_approval");
            result.Results.Single(test => test.Key == "duplicate_workflow_start")
                .StepObservations.Last().Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                    observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.ActionConflict &&
                    observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable &&
                    observation.ActualTarget == "submit_for_approval");
            (await records.ListRecordsAsync(
                tenant,
                "logs",
                new RuntimeRecordQuery(PageSize: 100))).Total.Should().Be(0, "the guard must roll back the earlier create_record action step");

            await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "select results::text from _runtime.test_results order by id desc limit 1";
            var persistedJson = (string)(await command.ExecuteScalarAsync())!;
            using var persisted = JsonDocument.Parse(persistedJson);
            var persistedTests = persisted.RootElement.EnumerateArray().ToArray();
            persistedTests.Should().HaveCount(5);
            foreach (var persistedTest in persistedTests)
            {
                persistedTest.TryGetProperty("stepObservations", out var observations).Should().BeTrue();
                observations.ValueKind.Should().Be(JsonValueKind.Array);
                observations.GetArrayLength().Should().BeGreaterThan(0);
            }
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_contract_executes_distinct_actors_and_exact_workflow_assertion_checkpoints()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_v3_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-v3",
                "accept-v3.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty exact fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "exact_actor_workflow",
                    Label = "Exact actors decide the exact workflow instance"
                },
                Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
                Actors =
                [
                    new BundleAcceptanceActor { Id = "requester", Roles = ["operator"] },
                    new BundleAcceptanceActor { Id = "decider", Roles = ["manager"] }
                ],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        AsActor = "requester",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"V3 Job {suffix}",
                            ["status"] = "draft"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "submit_job",
                        Type = "start_workflow_action",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Action = "submit_for_approval",
                        AsRole = "operator",
                        AsActor = "requester"
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "reject_job",
                        Type = "reject_task",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        WorkflowRef = "submit_job",
                        AsRole = "manager",
                        AsActor = "decider",
                        Expected = new BundleAcceptanceExpectedOutcome
                        {
                            Outcome = BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                            Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected,
                            Target = "manager_approval"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "read_job_report",
                        Type = "call_report",
                        Report = "jobs_summary",
                        AsRole = "operator",
                        AsActor = "requester"
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "job_is_rejected",
                        AfterStep = "reject_job",
                        Type = "record_field_equals",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Field = "status",
                        Value = "rejected"
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "exact_workflow_is_rejected",
                        AfterStep = "reject_job",
                        Type = "workflow_status",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Workflow = "job_approval",
                        WorkflowRef = "submit_job",
                        Status = "rejected"
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "exact_task_exists",
                        AfterStep = "reject_job",
                        Type = "task_count",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        WorkflowRef = "submit_job",
                        TaskStep = "manager_approval",
                        Status = "rejected",
                        Count = 1
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "report_uses_actor_result",
                        AfterStep = "read_job_report",
                        Type = "report_metric_equals",
                        Report = "jobs_summary",
                        ReportRef = "read_job_report",
                        Metric = "total_jobs",
                        Value = 1
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            result.SeedHash.Should().Be(seedHash);
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            runtimeTest.StepObservations.Should().HaveCount(4)
                .And.OnlyContain(observation => observation.ContractItemHash.Length == 64);
            runtimeTest.StepObservations.Select(observation => observation.PrincipalId)
                .Distinct().Should().HaveCount(2);
            runtimeTest.StepObservations.Single(observation => observation.StepId == "reject_job")
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.ActorId == "decider" &&
                observation.WorkflowRef == "submit_job" &&
                observation.WorkflowInstanceId != null &&
                observation.TaskId != null &&
                observation.RecordId != null);
            runtimeTest.AssertionObservations.Should().HaveCount(4)
                .And.OnlyContain(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed &&
                    observation.ContractItemHash.Length == 64);
            runtimeTest.AssertionObservations.Single(observation => observation.AssertionId == "exact_workflow_is_rejected")
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.WorkflowInstanceId != null &&
                    observation.WorkflowStatus == "rejected" &&
                    EvidenceMatches(
                        observation,
                        BundleAcceptanceEvidenceValueKinds.String,
                        "rejected",
                        "rejected"));
            EvidenceMatches(
                    runtimeTest.AssertionObservations.Single(observation => observation.AssertionId == "job_is_rejected"),
                    BundleAcceptanceEvidenceValueKinds.String,
                    "rejected",
                    "rejected")
                .Should().BeTrue();
            var taskObservation = runtimeTest.AssertionObservations.Single(observation =>
                observation.AssertionId == "exact_task_exists");
            taskObservation.TaskIds.Should().ContainSingle();
            taskObservation.TaskSteps.Should().Equal("manager_approval");
            taskObservation.TaskStatuses.Should().Equal("rejected");
            runtimeTest.AssertionObservations.Single(observation => observation.AssertionId == "report_uses_actor_result")
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.ReportRef == "read_job_report" &&
                    observation.ActorId == "requester" &&
                    observation.PrincipalId != null &&
                    observation.RowCount == 1 &&
                    observation.ReportMetricValues.Count == 0 &&
                    observation.ReportMetricDigests.Count == 1 &&
                    observation.ReportOutputHash.Length == 64);

            await using var persistedConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await persistedConnection.OpenAsync();
            await using var persistedCommand = persistedConnection.CreateCommand();
            persistedCommand.CommandText = "select results::text from _runtime.test_results order by id desc limit 1";
            var persistedJson = (string)(await persistedCommand.ExecuteScalarAsync())!;
            using var persisted = JsonDocument.Parse(persistedJson);
            var reportEvidence = persisted.RootElement[0]
                .GetProperty("assertionObservations")
                .EnumerateArray()
                .Single(observation => observation.GetProperty("assertionId").GetString() == "report_uses_actor_result");
            reportEvidence.GetProperty("reportOutputHash").GetString().Should().MatchRegex("^[a-f0-9]{64}$");
            reportEvidence.TryGetProperty("reportRows", out _).Should().BeFalse();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_notification_assertion_requires_the_exact_new_checkpoint_notification()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_notice_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-notice",
                "accept-notice.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var bundle = CreateExpectedOutcomeBundle();
            var workflow = bundle.Modules.SelectMany(module => module.Workflows)
                .Single(candidate => candidate.Id == "job_approval");
            workflow.OnRejected.Add(new ErpWorkflowStep
            {
                Id = "notify_job_rejected",
                Type = "notification_placeholder",
                Message = "The job was rejected."
            });
            var compiled = new ErpBundleCompiler().Compile(bundle);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty notification fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var fresh = NotificationTest("fresh_notice", "notify_job_rejected", seedHash);
            var stale = NotificationTest("stale_notice", "manager_approval", seedHash);
            BundleAcceptanceTestContractHasher.Stamp([fresh, stale]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [fresh, stale]);

            result.Status.Should().Be("failed");
            var freshResult = result.Results.Single(test => test.Key == fresh.Test.Key);
            freshResult.Status.Should().Be("passed");
            var freshObservation = freshResult.AssertionObservations.Should().ContainSingle().Which;
            freshObservation.Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed &&
                observation.WorkflowInstanceId != null &&
                observation.NotificationIds.Count == 1);
            freshObservation.Notifications.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestNotificationObservation>(notification =>
                    notification.Id == freshObservation.NotificationIds[0] &&
                    notification.TaskId == null &&
                    notification.NotificationType == "workflow_notice" &&
                    string.IsNullOrEmpty(notification.RecipientRole) &&
                    notification.RecipientUserId == null);
            EvidenceMatches(
                    freshObservation,
                    BundleAcceptanceEvidenceValueKinds.Integer,
                    1,
                    1)
                .Should().BeTrue();

            var staleResult = result.Results.Single(test => test.Key == stale.Test.Key);
            staleResult.Status.Should().Be("failed",
                "the manager_approval notification existed before the reject_job checkpoint");
            staleResult.AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    observation.NotificationIds.Count == 0 &&
                    observation.Detail == $"{RuntimeBundleTestEvidenceFailureCodes.AssertionFailed}:stale_notification");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }

        BundleAcceptanceTestDocument NotificationTest(string prefix, string taskStep, string exactSeedHash) => new()
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest
            {
                Key = $"{prefix}_{suffix}",
                Label = $"{prefix} notification correlation"
            },
            Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = exactSeedHash },
            Actors =
            [
                new BundleAcceptanceActor { Id = "requester", Roles = ["operator"] },
                new BundleAcceptanceActor { Id = "decider", Roles = ["manager"] }
            ],
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = $"{prefix}_create_job",
                    Type = "create_record",
                    Entity = "job",
                    AsRole = "operator",
                    AsActor = "requester",
                    Values = new Dictionary<string, object?>
                    {
                        ["name"] = $"Notification Job {prefix} {suffix}",
                        ["status"] = "draft"
                    }
                },
                new BundleAcceptanceStep
                {
                    Id = $"{prefix}_start_job",
                    Type = "start_workflow_action",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = $"{prefix}_create_job" },
                    Action = "submit_for_approval",
                    AsRole = "operator",
                    AsActor = "requester"
                },
                new BundleAcceptanceStep
                {
                    Id = $"{prefix}_reject_job",
                    Type = "reject_task",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = $"{prefix}_create_job" },
                    WorkflowRef = $"{prefix}_start_job",
                    AsRole = "manager",
                    AsActor = "decider",
                    Expected = new BundleAcceptanceExpectedOutcome
                    {
                        Outcome = BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                        Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected,
                        Target = "manager_approval"
                    }
                }
            ],
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Id = prefix == "fresh_notice" ? "fresh_notification" : "stale_notification",
                    AfterStep = $"{prefix}_reject_job",
                    Type = "notification_exists",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = $"{prefix}_create_job" },
                    WorkflowRef = $"{prefix}_start_job",
                    TaskStep = taskStep
                }
            ]
        };
    }

    [PostgresFact]
    public async Task V3_rejection_audit_assertions_bind_reserved_duplicate_events_to_exact_authored_steps()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_reject_audit_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-reject-audit",
                "accept-reject-audit.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var bundle = CreateExpectedOutcomeBundle();
            var workflow = bundle.Modules.SelectMany(module => module.Workflows)
                .Single(candidate => candidate.Id == "job_approval");
            workflow.OnRejected.AddRange(
            [
                new ErpWorkflowStep
                {
                    Id = "authored_reserved_rejection",
                    Type = "audit_event",
                    Event = "workflow.task.rejected",
                    Message = "Authored reserved rejection event."
                },
                new ErpWorkflowStep
                {
                    Id = "duplicate_reserved_rejection",
                    Type = "audit_event",
                    Event = "workflow.task.rejected",
                    Message = "Second authored event with the same key."
                }
            ]);
            var compiled = new ErpBundleCompiler().Compile(bundle);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty audit fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var exact = AuditTest(
                "exact_audit",
                seedHash,
                "authored_reserved_rejection",
                "duplicate_reserved_rejection");
            var missing = AuditTest("missing_audit", seedHash, "missing_authored_rejection");
            BundleAcceptanceTestContractHasher.Stamp([exact, missing]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [exact, missing]);

            result.Status.Should().Be("failed");
            var exactResult = result.Results.Single(test => test.Key == exact.Test.Key);
            exactResult.Status.Should().Be("passed");
            exactResult.AssertionObservations.Should().HaveCount(2)
                .And.OnlyContain(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed &&
                    observation.AuditEventIds.Count == 1 &&
                    (observation.AuditStep == "authored_reserved_rejection" ||
                     observation.AuditStep == "duplicate_reserved_rejection"));
            exactResult.AssertionObservations.SelectMany(observation => observation.AuditEventIds)
                .Should().OnlyHaveUniqueItems();

            var missingResult = result.Results.Single(test => test.Key == missing.Test.Key);
            missingResult.Status.Should().Be("failed",
                "the built-in workflow.task.rejected event must not satisfy an authored-step assertion");
            missingResult.AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    observation.AuditStep == "missing_authored_rejection" &&
                    observation.AuditEventIds.Count == 0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }

        BundleAcceptanceTestDocument AuditTest(
            string prefix,
            string exactSeedHash,
            params string[] auditSteps)
        {
            return new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = $"{prefix}_{suffix}",
                    Label = $"{prefix} rejection audit correlation"
                },
                Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = exactSeedHash },
                Actors =
                [
                    new BundleAcceptanceActor { Id = "requester", Roles = ["operator"] },
                    new BundleAcceptanceActor { Id = "decider", Roles = ["manager"] }
                ],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = $"{prefix}_create_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        AsActor = "requester",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Audit Job {prefix} {suffix}",
                            ["status"] = "draft"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = $"{prefix}_start_job",
                        Type = "start_workflow_action",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = $"{prefix}_create_job" },
                        Action = "submit_for_approval",
                        AsRole = "operator",
                        AsActor = "requester"
                    },
                    new BundleAcceptanceStep
                    {
                        Id = $"{prefix}_reject_job",
                        Type = "reject_task",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = $"{prefix}_create_job" },
                        WorkflowRef = $"{prefix}_start_job",
                        AsRole = "manager",
                        AsActor = "decider",
                        Expected = new BundleAcceptanceExpectedOutcome
                        {
                            Outcome = BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                            Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected,
                            Target = "manager_approval"
                        }
                    }
                ],
                Assertions = auditSteps.Select((auditStep, index) => new BundleAcceptanceAssertion
                {
                    Id = $"{prefix}_audit_{index + 1}",
                    AfterStep = $"{prefix}_reject_job",
                    Type = "audit_event_exists",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = $"{prefix}_create_job" },
                    Event = "workflow.task.rejected",
                    AuditStep = auditStep
                }).ToList()
            };
        }
    }

    [PostgresFact]
    public async Task V3_actor_permissions_come_from_durable_identity_and_owned_actors_are_exactly_cleaned()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var testKey = $"t{new string('k', 127)}";
        var actorId = $"a{new string('c', 127)}";
        var database = $"erp_tenant_accept_actor_identity_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-actor-identity",
                "accept-actor-identity.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            await using (var legacyMarkerConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await legacyMarkerConnection.OpenAsync();
                await using var legacyMarkerCommand = legacyMarkerConnection.CreateCommand();
                legacyMarkerCommand.CommandText = """
                    create table _runtime.acceptance_test_actor_ownership (
                      run_id uuid not null,
                      test_key varchar(120) not null,
                      actor_id varchar(120) not null,
                      runtime_user_id bigint not null,
                      email varchar(320) not null,
                      role_id varchar(80) not null,
                      primary key (run_id, runtime_user_id),
                      unique (run_id, test_key, actor_id)
                    )
                    """;
                await legacyMarkerCommand.ExecuteNonQueryAsync();
            }

            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty durable actor fixture" }
            };
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = testKey,
                    Label = "Durable actor revocation overrides the reloaded execution context"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = seed.Seed.Key,
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors =
                [
                    new BundleAcceptanceActor { Id = actorId, Roles = ["operator"] }
                ],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_job",
                        Type = "create_record",
                        Entity = "job",
                        AsActor = actorId,
                        AsRole = "operator",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Durable identity job {suffix}",
                            ["status"] = "open"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "revoked_update",
                        Type = "update_record",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        AsActor = actorId,
                        AsRole = "operator",
                        Set = new Dictionary<string, object?> { ["status"] = "closed" },
                        Expected = new BundleAcceptanceExpectedOutcome
                        {
                            Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                            Code = BundleAcceptanceExpectedOutcomeCodes.PermissionEntityUpdate,
                            Target = "job"
                        }
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "revoked_update_did_not_write",
                        AfterStep = "revoked_update",
                        Type = "record_field_equals",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Field = "status",
                        Value = "open"
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);
            var suite = BundleAcceptanceTestContractHasher.Verify([test]);
            var actorPrincipalId = BundleAcceptanceActorPrincipalId.Derive(
                suite.SuiteHash,
                test.Test.Key,
                actorId);

            await using (var triggerConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await triggerConnection.OpenAsync();
                await using var triggerCommand = triggerConnection.CreateCommand();
                triggerCommand.CommandText = $"""
                    create function app.revoke_acceptance_actor_role()
                    returns trigger
                    language plpgsql
                    as $revoke_actor$
                    begin
                      delete from _runtime.runtime_user_roles
                      where user_id = {actorPrincipalId.ToString(System.Globalization.CultureInfo.InvariantCulture)};
                      return new;
                    end
                    $revoke_actor$;

                    create trigger revoke_acceptance_actor_role_after_job_create
                    after insert on app.jobs
                    for each row execute function app.revoke_acceptance_actor_role()
                    """;
                await triggerCommand.ExecuteNonQueryAsync();
            }

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var revoked = result.Results.Single().StepObservations.Single(observation =>
                observation.StepId == "revoked_update");
            revoked.Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.PermissionDenied &&
                observation.PrincipalId == actorPrincipalId &&
                observation.ActorRoles.SequenceEqual(new[] { "operator" }));

            await using var verifyConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = """
                select
                  (select count(*) from _runtime.runtime_users),
                  (select count(*) from _runtime.runtime_user_roles),
                  (select count(*) from _runtime.runtime_user_links),
                  (select count(*) from _runtime.acceptance_test_actor_ownership)
                """;
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(0);
            reader.GetInt64(1).Should().Be(0);
            reader.GetInt64(2).Should().Be(0);
            reader.GetInt64(3).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_bind_actor_record_drives_linked_scope_authorization_without_business_audit()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_actor_bind_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-actor-bind",
                "accept-actor-bind.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var bundle = CreateExpectedOutcomeBundle();
            bundle.Roles["linked_operator"].Grants.Add(new ErpGrant
            {
                Action = "update",
                Entity = "job",
                Scope = PermissionGrantScopes.Record,
                RecordScope = new ErpRecordScope
                {
                    Field = "customer",
                    Source = RecordPermissionScopeSources.LinkedRecord,
                    LinkedEntity = "customer"
                }
            });
            var compiled = new ErpBundleCompiler().Compile(bundle);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            await using (var guardConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await guardConnection.OpenAsync();
                await using var guardCommand = guardConnection.CreateCommand();
                guardCommand.CommandText = """
                    alter table audit.events
                    add constraint reject_acceptance_harness_link_audit
                    check (event_type <> 'runtime_user.linked_record')
                    """;
                await guardCommand.ExecuteNonQueryAsync();
            }

            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty linked-actor fixture" }
            };
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "linked_actor_report_isolation",
                    Label = "A bound actor can create and read only records linked through its exact customer"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = seed.Seed.Key,
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors =
                [
                    new BundleAcceptanceActor { Id = "linked_actor", Roles = ["linked_operator"] },
                    new BundleAcceptanceActor { Id = "unbound_actor", Roles = ["linked_operator"] }
                ],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_customer",
                        Type = BundleAcceptanceStepTypes.CreateRecord,
                        Entity = "customer",
                        AsActor = "linked_actor",
                        AsRole = "linked_operator",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Linked customer {suffix}"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "bind_linked_actor",
                        Type = BundleAcceptanceStepTypes.BindActorRecord,
                        Entity = "customer",
                        Record = new BundleRecordSelector { Ref = "create_customer" },
                        AsActor = "linked_actor",
                        AsRole = "linked_operator"
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "create_linked_job",
                        Type = BundleAcceptanceStepTypes.CreateRecord,
                        Entity = "job",
                        AsActor = "linked_actor",
                        AsRole = "linked_operator",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Linked job {suffix}",
                            ["status"] = "open",
                            ["customer"] = new Dictionary<string, object?>
                            {
                                ["ref"] = "create_customer"
                            }
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "unbound_actor_create_denied",
                        Type = BundleAcceptanceStepTypes.CreateRecord,
                        Entity = "job",
                        AsActor = "unbound_actor",
                        AsRole = "linked_operator",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Denied linked job {suffix}",
                            ["status"] = "open",
                            ["customer"] = new Dictionary<string, object?>
                            {
                                ["ref"] = "create_customer"
                            }
                        },
                        Expected = new BundleAcceptanceExpectedOutcome
                        {
                            Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                            Code = BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate,
                            Target = "job"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "unbound_actor_update_denied",
                        Type = "update_record",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_linked_job" },
                        AsActor = "unbound_actor",
                        AsRole = "linked_operator",
                        Set = new Dictionary<string, object?> { ["status"] = "denied" },
                        Expected = new BundleAcceptanceExpectedOutcome
                        {
                            Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                            Code = BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate,
                            Target = "job"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "linked_actor_update",
                        Type = "update_record",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_linked_job" },
                        AsActor = "linked_actor",
                        AsRole = "linked_operator",
                        Set = new Dictionary<string, object?> { ["status"] = "assigned" }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "linked_actor_report",
                        Type = "call_report",
                        Report = "jobs_summary",
                        AsActor = "linked_actor",
                        AsRole = "linked_operator"
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "unbound_actor_report",
                        Type = "call_report",
                        Report = "jobs_summary",
                        AsActor = "unbound_actor",
                        AsRole = "linked_operator"
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "linked_actor_update_persisted",
                        AfterStep = "linked_actor_update",
                        Type = "record_field_equals",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_linked_job" },
                        Field = "status",
                        Value = "assigned"
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "linked_actor_sees_job",
                        AfterStep = "linked_actor_report",
                        Type = "report_metric_equals",
                        Report = "jobs_summary",
                        ReportRef = "linked_actor_report",
                        Metric = "total_jobs",
                        Value = 1
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "unbound_actor_sees_no_job",
                        AfterStep = "unbound_actor_report",
                        Type = "report_metric_equals",
                        Report = "jobs_summary",
                        ReportRef = "unbound_actor_report",
                        Metric = "total_jobs",
                        Value = 0
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);
            var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([test], compiled);
            validate.Should().NotThrow();

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be(
                "passed",
                string.Join("; ", result.Results.SelectMany(item => item.Failures)));
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            runtimeTest.StepObservations.Single(observation => observation.StepId == "bind_linked_actor")
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                    observation.ActorId == "linked_actor" &&
                    observation.RecordRef == "create_customer" &&
                    observation.RecordId != null &&
                    observation.ProducedRecordRef.Length == 0 &&
                    observation.ProducedRecordId == null);
            runtimeTest.StepObservations.Single(observation => observation.StepId == "unbound_actor_create_denied")
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                    observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.PermissionDenied &&
                    observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate &&
                    observation.ActualTarget == "job" &&
                    observation.ProducedRecordId == null);
            runtimeTest.StepObservations.Single(observation => observation.StepId == "unbound_actor_update_denied")
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                    observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.PermissionDenied &&
                    observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate &&
                    observation.ActualTarget == "job");
            runtimeTest.AssertionObservations.Single(observation => observation.AssertionId == "linked_actor_sees_job")
                .ActorId.Should().Be("linked_actor");
            runtimeTest.AssertionObservations.Single(observation => observation.AssertionId == "unbound_actor_sees_no_job")
                .ActorId.Should().Be("unbound_actor");

            await using var verifyConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = """
                select
                  (select count(*) from _runtime.runtime_users),
                  (select count(*) from _runtime.runtime_user_roles),
                  (select count(*) from _runtime.runtime_user_links),
                  (select count(*) from _runtime.acceptance_test_actor_ownership)
                """;
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(0);
            reader.GetInt64(1).Should().Be(0);
            reader.GetInt64(2).Should().Be(0);
            reader.GetInt64(3).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_exact_report_and_dashboard_assertions_prove_outputs_and_fail_adversarial_variants()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_exact_reports_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-exact-reports",
                "accept-exact-reports.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty exact report fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);

            var passing = ExactReportTest(
                "exact_report_primitives_pass",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Exact report job {suffix}"),
                    ExactCallReportStep("call_jobs", "jobs_summary"),
                    ExactCallReportStep("call_activities", "activity_by_job")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "one_job_row",
                        AfterStep = "call_jobs",
                        Type = "report_row_count",
                        Report = "jobs_summary",
                        ReportRef = "call_jobs",
                        Count = 1
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "job_absent_from_activity",
                        AfterStep = "call_activities",
                        Type = "report_record_absent",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Report = "activity_by_job",
                        ReportRef = "call_activities",
                        Metric = "job"
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "dashboard_job_total",
                        AfterStep = "call_jobs",
                        Type = "dashboard_metric_equals",
                        Widget = "jobs_total",
                        Report = "jobs_summary",
                        ReportRef = "call_jobs",
                        Metric = "total_jobs",
                        Value = 1
                    }
                ]);
            var wrongCount = ExactReportTest(
                "wrong_report_count",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Wrong count job {suffix}"),
                    ExactCallReportStep("call_jobs", "jobs_summary")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "expects_two_rows",
                        AfterStep = "call_jobs",
                        Type = "report_row_count",
                        Report = "jobs_summary",
                        ReportRef = "call_jobs",
                        Count = 2
                    }
                ]);
            var presentForbiddenRecord = ExactReportTest(
                "present_forbidden_report_record",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Present report job {suffix}"),
                    ExactCreateActivityStep("create_activity", "create_job"),
                    ExactCallReportStep("call_activities", "activity_by_job")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "job_must_be_absent",
                        AfterStep = "call_activities",
                        Type = "report_record_absent",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Report = "activity_by_job",
                        ReportRef = "call_activities",
                        Metric = "job"
                    }
                ]);
            var mismatchedWidget = ExactReportTest(
                "mismatched_dashboard_widget_binding",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Mismatched widget job {suffix}"),
                    ExactCreateActivityStep("create_activity", "create_job"),
                    ExactCallReportStep("call_activities", "activity_by_job")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "wrong_widget_binding",
                        AfterStep = "call_activities",
                        Type = "dashboard_metric_equals",
                        Widget = "jobs_total",
                        Report = "activity_by_job",
                        ReportRef = "call_activities",
                        Metric = "total_activities",
                        Value = 1
                    }
                ]);
            var wrongMetric = ExactReportTest(
                "wrong_dashboard_metric",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Wrong metric job {suffix}"),
                    ExactCallReportStep("call_jobs", "jobs_summary")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "missing_dashboard_metric",
                        AfterStep = "call_jobs",
                        Type = "dashboard_metric_equals",
                        Widget = "jobs_total",
                        Report = "jobs_summary",
                        ReportRef = "call_jobs",
                        Metric = "missing_metric",
                        Value = 1
                    }
                ]);
            var wrongValue = ExactReportTest(
                "wrong_dashboard_value",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Wrong dashboard value job {suffix}"),
                    ExactCallReportStep("call_jobs", "jobs_summary")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "incorrect_dashboard_value",
                        AfterStep = "call_jobs",
                        Type = "dashboard_metric_equals",
                        Widget = "jobs_total",
                        Report = "jobs_summary",
                        ReportRef = "call_jobs",
                        Metric = "total_jobs",
                        Value = 2
                    }
                ]);
            var wrongActor = ExactReportTest(
                "wrong_report_actor",
                seedHash,
                [ExactCallReportStep("restricted_call_jobs", "jobs_summary", "restricted_actor", "restricted")],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "restricted_report_count",
                        AfterStep = "restricted_call_jobs",
                        Type = "report_row_count",
                        Report = "jobs_summary",
                        ReportRef = "restricted_call_jobs",
                        Count = 0
                    }
                ]);
            var aggregateSpoofedAsIdentity = ExactReportTest(
                "aggregate_spoofed_as_identity",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Aggregate identity spoof {suffix}"),
                    ExactCallReportStep("call_jobs", "jobs_summary")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "aggregate_is_not_identity",
                        AfterStep = "call_jobs",
                        Type = "report_record_absent",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Report = "jobs_summary",
                        ReportRef = "call_jobs",
                        Metric = "total_jobs"
                    }
                ]);
            var wrongIdentityEntity = ExactReportTest(
                "wrong_identity_entity",
                seedHash,
                [
                    ExactCreateJobStep("create_job", $"Wrong identity entity {suffix}"),
                    ExactCreateActivityStep("create_activity", "create_job"),
                    ExactCallReportStep("call_activities", "activity_by_job")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "job_identity_is_not_activity_identity",
                        AfterStep = "call_activities",
                        Type = "report_record_absent",
                        Entity = "activity_entry",
                        Record = new BundleRecordSelector { Ref = "create_activity" },
                        Report = "activity_by_job",
                        ReportRef = "call_activities",
                        Metric = "job"
                    }
                ]);
            List<BundleAcceptanceTestDocument> tests =
            [
                passing,
                wrongCount,
                presentForbiddenRecord,
                mismatchedWidget,
                wrongMetric,
                wrongValue,
                wrongActor,
                aggregateSpoofedAsIdentity,
                wrongIdentityEntity
            ];
            BundleAcceptanceTestContractHasher.Stamp(tests);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                tests);

            result.Status.Should().Be("failed");
            result.Passed.Should().Be(1);
            result.Failed.Should().Be(8);
            var results = result.Results.ToDictionary(test => test.Key, StringComparer.Ordinal);
            var passingResult = results[passing.Test.Key];
            passingResult.Status.Should().Be("passed");
            passingResult.AssertionObservations.Should().HaveCount(3)
                .And.OnlyContain(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed &&
                    observation.ReportOutputHash.Length == 64 &&
                    observation.ActorId == "operator_actor" &&
                    observation.PrincipalId != null);
            passingResult.AssertionObservations.Single(observation => observation.AssertionId == "one_job_row")
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 1, 1) &&
                    observation.RowCount == 1);
            passingResult.AssertionObservations.Single(observation => observation.AssertionId == "job_absent_from_activity")
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.RecordId != null &&
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 0, 0) &&
                    observation.RowCount == 0 &&
                    observation.ReportMetricValues.Count == 0 &&
                    observation.ReportMetricDigests.Count == 0);
            passingResult.AssertionObservations.Single(observation => observation.AssertionId == "dashboard_job_total")
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Widget == "jobs_total" &&
                    observation.DashboardWidgetReport == "jobs_summary" &&
                    observation.DashboardWidgetMetric == "total_jobs" &&
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 1, 1) &&
                    observation.RowCount == 1 &&
                    observation.ReportMetricValues.Count == 0 &&
                    observation.ReportMetricDigests.Count == 1);

            results[wrongCount.Test.Key].AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 2, 1));
            results[presentForbiddenRecord.Test.Key].AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 0, 1) &&
                    observation.ReportMetricValues.Count == 0 &&
                    observation.ReportMetricDigests.Count == 1);
            results[mismatchedWidget.Test.Key].AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    observation.DashboardWidgetReport == "jobs_summary" &&
                    observation.DashboardWidgetMetric == "total_jobs" &&
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 1, 1));
            results[wrongMetric.Test.Key].AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    observation.Metric == "missing_metric" &&
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 1, null) &&
                    observation.DashboardWidgetMetric == "total_jobs");
            results[wrongValue.Test.Key].AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    EvidenceMatches(observation, BundleAcceptanceEvidenceValueKinds.Integer, 2, 1));
            results[wrongActor.Test.Key].AssertionObservations.Should().BeEmpty();
            results[wrongActor.Test.Key].StepObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Failed &&
                    observation.ActorId == "restricted_actor" &&
                    observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.PermissionReportExecute);
            results[aggregateSpoofedAsIdentity.Test.Key].Failures.Should().Equal(
                $"{RuntimeBundleTestEvidenceFailureCodes.AssertionFailed}:aggregate_is_not_identity");
            results[wrongIdentityEntity.Test.Key].Failures.Should().Equal(
                $"{RuntimeBundleTestEvidenceFailureCodes.AssertionFailed}:job_identity_is_not_activity_identity");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_returned_and_persisted_evidence_never_contains_raw_business_values_or_exception_messages()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_digest_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        var passedSecret = $"passed-secret-{suffix}";
        var failedExpectedSecret = $"failed-expected-secret-{suffix}";
        var failedActualSecret = $"failed-actual-secret-{suffix}";
        var exceptionSecret = $"exception-secret-{suffix}";
        var sentinels = new[]
        {
            passedSecret,
            failedExpectedSecret,
            failedActualSecret,
            exceptionSecret
        };

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-digest",
                "accept-digest.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var bundle = CreateExpectedOutcomeBundle();
            bundle.Reports.Add(new ErpReport
            {
                Key = "jobs_by_name",
                Label = "Jobs by name",
                Source = "job",
                GroupBy = [new ErpReportGroupBy { Field = "name", Label = "Name" }],
                Metrics = [new ErpReportMetric { Key = "total_jobs", Label = "Total jobs", Op = "count" }]
            });
            bundle.Actions.Add(new ErpAction
            {
                Key = "guard_with_secret_error",
                Label = "Guard with confidential error",
                Entity = "job",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions = [new ErpGrant { Action = "read", Entity = "job" }]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "confidential_guard",
                        Type = "guard",
                        Condition = new ErpActionCondition
                        {
                            Op = "eq",
                            Left = new ErpActionOperand { Value = false },
                            Right = new ErpActionOperand { Value = true }
                        },
                        Error = exceptionSecret
                    }
                ]
            });
            bundle.Roles["operator"].Grants.Add(new ErpGrant
            {
                Action = GranularPermissionActions.Execute,
                Entity = "job",
                Scope = PermissionGrantScopes.Action,
                Target = "guard_with_secret_error"
            });
            var compiled = new ErpBundleCompiler().Compile(bundle);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty confidentiality fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var passing = ExactReportTest(
                "digest_only_pass",
                seedHash,
                [
                    ExactCreateJobStep("create_passed_job", passedSecret),
                    ExactCallReportStep("call_jobs_by_name", "jobs_by_name")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "passed_field_value",
                        AfterStep = "create_passed_job",
                        Type = "record_field_equals",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_passed_job" },
                        Field = "name",
                        Value = passedSecret
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "passed_report_value",
                        AfterStep = "call_jobs_by_name",
                        Type = "report_metric_equals",
                        Report = "jobs_by_name",
                        ReportRef = "call_jobs_by_name",
                        Metric = "name",
                        Value = passedSecret
                    }
                ]);
            var failing = ExactReportTest(
                "digest_only_failure",
                seedHash,
                [ExactCreateJobStep("create_failed_job", failedActualSecret)],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "failed_field_value",
                        AfterStep = "create_failed_job",
                        Type = "record_field_equals",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_failed_job" },
                        Field = "name",
                        Value = failedExpectedSecret
                    }
                ]);
            var throwing = ExactReportTest(
                "digest_only_exception",
                seedHash,
                [
                    ExactCreateJobStep("create_guard_job", $"Guard job {suffix}"),
                    new BundleAcceptanceStep
                    {
                        Id = "run_secret_guard",
                        Type = "run_action",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_guard_job" },
                        Action = "guard_with_secret_error",
                        AsRole = "operator",
                        AsActor = "operator_actor"
                    }
                ],
                []);
            List<BundleAcceptanceTestDocument> tests = [passing, failing, throwing];
            BundleAcceptanceTestContractHasher.Stamp(tests);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                tests);
            var returnedJson = JsonSerializer.Serialize(result);
            await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await connection.OpenAsync();
            await using var persistedCommand = connection.CreateCommand();
            persistedCommand.CommandText = "select results::text from _runtime.test_results order by id desc limit 1";
            var persistedJson = (string)(await persistedCommand.ExecuteScalarAsync())!;

            result.Status.Should().Be("failed");
            result.Passed.Should().Be(1);
            result.Failed.Should().Be(2);
            foreach (var sentinel in sentinels)
            {
                returnedJson.Should().NotContain(sentinel);
                persistedJson.Should().NotContain(sentinel);
            }

            var passedResult = result.Results.Single(item => item.Key == passing.Test.Key);
            var passedField = passedResult.AssertionObservations.Single(item => item.AssertionId == "passed_field_value");
            passedField.ExpectedValue.Should().BeNull();
            passedField.ActualValue.Should().BeNull();
            passedField.ExpectedValueDigestSchema.Should().Be(BundleAcceptanceEvidenceDigestContract.Schema);
            passedField.ActualValueDigestSchema.Should().Be(BundleAcceptanceEvidenceDigestContract.Schema);
            passedField.ExpectedValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.String);
            passedField.ExpectedValueDigest.Should().Be(
                BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.String,
                    passedSecret).Digest);
            passedField.ActualValueDigest.Should().Be(passedField.ExpectedValueDigest);
            passedField.ContractItemHash.Should().MatchRegex("^[a-f0-9]{64}$");
            var reportEvidence = passedResult.AssertionObservations.Single(item => item.AssertionId == "passed_report_value");
            reportEvidence.ReportMetricValues.Should().BeEmpty();
            reportEvidence.ReportMetricDigests.Should().ContainSingle().Which
                .Should().Be(BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.String,
                    passedSecret));
            reportEvidence.ReportOutputHash.Should().MatchRegex("^[a-f0-9]{64}$");

            var failedResult = result.Results.Single(item => item.Key == failing.Test.Key);
            failedResult.Failures.Should().Equal(
                $"{RuntimeBundleTestEvidenceFailureCodes.AssertionFailed}:failed_field_value");
            failedResult.AssertionObservations.Single().Detail.Should().Be(
                $"{RuntimeBundleTestEvidenceFailureCodes.AssertionFailed}:failed_field_value");
            var thrownResult = result.Results.Single(item => item.Key == throwing.Test.Key);
            thrownResult.Failures.Should().Equal(
                $"{RuntimeBundleTestEvidenceFailureCodes.StepFailed}:run_secret_guard");
            thrownResult.StepObservations.Last().Detail.Should().Be(
                $"{RuntimeBundleTestEvidenceFailureCodes.StepFailed}:run_secret_guard");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_report_evidence_kinds_follow_compiled_date_identity_and_actor_semantics()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_report_kinds_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            const string scheduledOn = "2026-01-02";
            var tenant = RuntimeTestTenant.Create(
                "accept-report-kinds",
                "accept-report-kinds.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var bundle = CreateExpectedOutcomeBundle();
            bundle.Modules.SelectMany(module => module.Entities)
                .Single(entity => entity.Id == "job")
                .Fields.Add(new ErpField
                {
                    Id = "scheduled_on",
                    Name = "scheduled_on",
                    DisplayName = "Scheduled on",
                    Type = "date",
                    Required = true
                });
            bundle.Reports.Add(new ErpReport
            {
                Key = "jobs_by_schedule",
                Label = "Jobs by schedule",
                Source = "job",
                GroupBy = [new ErpReportGroupBy { Field = "scheduled_on", Label = "Scheduled on" }],
                Metrics =
                [
                    new ErpReportMetric
                    {
                        Key = "earliest_schedule",
                        Label = "Earliest schedule",
                        Op = "min",
                        Field = "scheduled_on"
                    }
                ]
            });
            bundle.Reports.Add(new ErpReport
            {
                Key = "jobs_by_owner",
                Label = "Jobs by owner",
                Source = "job",
                GroupBy = [new ErpReportGroupBy { Field = "owner_principal", Label = "Owner principal" }],
                Metrics =
                [
                    new ErpReportMetric
                    {
                        Key = "lowest_owner_principal",
                        Label = "Lowest owner principal",
                        Op = "min",
                        Field = "owner_principal"
                    },
                    new ErpReportMetric
                    {
                        Key = "owner_principal_sum",
                        Label = "Owner principal sum",
                        Op = "sum",
                        Field = "owner_principal"
                    },
                    new ErpReportMetric
                    {
                        Key = "owner_count",
                        Label = "Owner count",
                        Op = "count",
                        Field = "owner_principal"
                    }
                ]
            });
            bundle.Dashboard.Widgets.Add(new ErpDashboardWidget
            {
                Key = "earliest_schedule",
                Label = "Earliest schedule",
                Type = "metric",
                Report = "jobs_by_schedule",
                Metric = "earliest_schedule"
            });
            bundle.Dashboard.Widgets.Add(new ErpDashboardWidget
            {
                Key = "lowest_owner_principal",
                Label = "Lowest owner principal",
                Type = "metric",
                Report = "jobs_by_owner",
                Metric = "lowest_owner_principal"
            });
            var compiled = new ErpBundleCompiler().Compile(bundle);
            var compiledOwnerReport = compiled.Reports.Single(report => report.Key == "jobs_by_owner");
            compiledOwnerReport.GroupBy.Single().EvidenceValueKind
                .Should().Be(BundleAcceptanceEvidenceValueKinds.ActorPrincipal);
            compiledOwnerReport.Metrics.Single(metric => metric.Key == "lowest_owner_principal")
                .EvidenceValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.ActorPrincipal);
            compiledOwnerReport.Metrics.Single(metric => metric.Key == "owner_principal_sum")
                .EvidenceValueKind.Should().BeNull();
            compiledOwnerReport.Metrics.Single(metric => metric.Key == "owner_count")
                .EvidenceValueKind.Should().BeNull();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty report-kind fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);

            var createScheduledJob = ExactCreateJobStep("create_scheduled_job", $"Scheduled job {suffix}");
            createScheduledJob.Values["scheduled_on"] = scheduledOn;
            var dateContract = ExactReportTest(
                "date_report_evidence_kind",
                seedHash,
                [createScheduledJob, ExactCallReportStep("call_schedule", "jobs_by_schedule")],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "schedule_group_is_date",
                        AfterStep = "call_schedule",
                        Type = "report_metric_equals",
                        Report = "jobs_by_schedule",
                        ReportRef = "call_schedule",
                        Metric = "scheduled_on",
                        Value = scheduledOn
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "schedule_dashboard_is_date",
                        AfterStep = "call_schedule",
                        Type = "dashboard_metric_equals",
                        Widget = "earliest_schedule",
                        Report = "jobs_by_schedule",
                        ReportRef = "call_schedule",
                        Metric = "earliest_schedule",
                        Value = scheduledOn
                    }
                ]);

            var actorToken = BundleAcceptanceActorPrincipalToken.For("operator_actor");
            var createOwnedJob = ExactCreateJobStep("create_owned_job", $"Owned job {suffix}");
            createOwnedJob.Values["scheduled_on"] = scheduledOn;
            createOwnedJob.Values["owner_principal"] = actorToken;
            var actorContract = ExactReportTest(
                "actor_report_evidence_kind",
                seedHash,
                [createOwnedJob, ExactCallReportStep("call_owners", "jobs_by_owner")],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "owner_group_is_actor",
                        AfterStep = "call_owners",
                        Type = "report_metric_equals",
                        Report = "jobs_by_owner",
                        ReportRef = "call_owners",
                        Metric = "owner_principal",
                        Value = actorToken
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "owner_dashboard_is_actor",
                        AfterStep = "call_owners",
                        Type = "dashboard_metric_equals",
                        Widget = "lowest_owner_principal",
                        Report = "jobs_by_owner",
                        ReportRef = "call_owners",
                        Metric = "lowest_owner_principal",
                        Value = actorToken
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "owner_sum_is_not_actor_evidence",
                        AfterStep = "call_owners",
                        Type = "report_metric_equals",
                        Report = "jobs_by_owner",
                        ReportRef = "call_owners",
                        Metric = "owner_principal_sum",
                        Value = actorToken
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "owner_count_is_not_actor_evidence",
                        AfterStep = "call_owners",
                        Type = "report_metric_equals",
                        Report = "jobs_by_owner",
                        ReportRef = "call_owners",
                        Metric = "owner_count",
                        Value = actorToken
                    }
                ]);

            var createIdentityJob = ExactCreateJobStep("create_identity_job", $"Identity job {suffix}");
            createIdentityJob.Values["scheduled_on"] = scheduledOn;
            var identityContract = ExactReportTest(
                "identity_report_evidence_kind",
                seedHash,
                [
                    createIdentityJob,
                    ExactCreateActivityStep("create_identity_activity", "create_identity_job"),
                    ExactCallReportStep("call_identity_report", "activity_by_job")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "job_identity_is_record_ref",
                        AfterStep = "call_identity_report",
                        Type = "report_metric_equals",
                        Report = "activity_by_job",
                        ReportRef = "call_identity_report",
                        Metric = "job",
                        ValueRef = "create_identity_job"
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "present_identity_still_uses_record_ref_digest",
                        AfterStep = "call_identity_report",
                        Type = "report_record_absent",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_identity_job" },
                        Report = "activity_by_job",
                        ReportRef = "call_identity_report",
                        Metric = "job"
                    }
                ]);
            List<BundleAcceptanceTestDocument> tests = [dateContract, actorContract, identityContract];
            BundleAcceptanceTestContractHasher.Stamp(tests);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                tests);

            result.Passed.Should().Be(1);
            result.Failed.Should().Be(2);
            var results = result.Results.ToDictionary(test => test.Key, StringComparer.Ordinal);

            foreach (var observation in results[dateContract.Test.Key].AssertionObservations)
            {
                EvidenceMatches(
                        observation,
                        BundleAcceptanceEvidenceValueKinds.Date,
                        scheduledOn,
                        scheduledOn)
                    .Should().BeTrue();
                var dateDigest = observation.ReportMetricDigests.Should().ContainSingle().Which;
                dateDigest.Should().Be(BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.Date,
                    scheduledOn));
                dateDigest.Should().NotBe(BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.String,
                    scheduledOn));
            }

            var actorResult = results[actorContract.Test.Key];
            actorResult.StepObservations.Select(observation => observation.StepId)
                .Should().Contain("call_owners", string.Join("; ", actorResult.Failures));
            actorResult.StepObservations.Single(observation => observation.StepId == "call_owners")
                .Status.Should().Be(
                    RuntimeBundleTestStepObservationStatuses.Passed,
                    string.Join("; ", actorResult.Failures));
            actorResult.AssertionObservations.Select(observation => observation.AssertionId)
                .Should().BeEquivalentTo(
                    "owner_group_is_actor",
                    "owner_dashboard_is_actor",
                    "owner_sum_is_not_actor_evidence",
                    "owner_count_is_not_actor_evidence");
            var actorPrincipal = actorResult.StepObservations
                .Single(observation => observation.StepId == "create_owned_job")
                .PrincipalId;
            actorPrincipal.Should().NotBeNull();
            var actorPrincipalValue = actorPrincipal!.Value;
            foreach (var observation in actorResult.AssertionObservations.Where(observation =>
                         observation.AssertionId is "owner_group_is_actor" or "owner_dashboard_is_actor"))
            {
                observation.Status.Should().Be(RuntimeBundleTestAssertionObservationStatuses.Passed);
                EvidenceMatches(
                        observation,
                        BundleAcceptanceEvidenceValueKinds.ActorPrincipal,
                        actorPrincipalValue,
                        actorPrincipalValue)
                    .Should().BeTrue();
                var actorDigest = observation.ReportMetricDigests.Should().ContainSingle().Which;
                actorDigest.Should().Be(BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.ActorPrincipal,
                    actorPrincipalValue));
                actorDigest.Should().NotBe(BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.Decimal,
                    actorPrincipalValue));
            }
            var actorSum = actorResult.AssertionObservations.Single(observation =>
                observation.AssertionId == "owner_sum_is_not_actor_evidence");
            EvidenceMatches(
                    actorSum,
                    BundleAcceptanceEvidenceValueKinds.ActorPrincipal,
                    actorPrincipalValue,
                    BundleAcceptanceEvidenceValueKinds.Decimal,
                    actorPrincipalValue)
                .Should().BeTrue("sum remains numeric even when its value equals the actor principal");
            actorSum.Status.Should().Be(RuntimeBundleTestAssertionObservationStatuses.Failed);
            actorSum.ReportMetricDigests.Should().ContainSingle().Which.Should().Be(
                BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.Decimal,
                    actorPrincipalValue));
            var actorCount = actorResult.AssertionObservations.Single(observation =>
                observation.AssertionId == "owner_count_is_not_actor_evidence");
            actorCount.ExpectedValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.ActorPrincipal);
            actorCount.ActualValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.Integer);
            actorCount.Status.Should().Be(RuntimeBundleTestAssertionObservationStatuses.Failed);
            actorCount.ReportMetricDigests.Should().ContainSingle().Which.Kind
                .Should().Be(BundleAcceptanceEvidenceValueKinds.Integer);

            var identityResult = results[identityContract.Test.Key];
            var recordId = identityResult.StepObservations
                .Single(observation => observation.StepId == "create_identity_job")
                .ProducedRecordId;
            recordId.Should().NotBeNull();
            var recordIdValue = recordId!.Value;
            var identityObservation = identityResult.AssertionObservations.Single(observation =>
                observation.AssertionId == "job_identity_is_record_ref");
            EvidenceMatches(
                    identityObservation,
                    BundleAcceptanceEvidenceValueKinds.RecordRef,
                    recordIdValue,
                    recordIdValue)
                .Should().BeTrue();
            var identityDigest = identityObservation.ReportMetricDigests.Should().ContainSingle().Which;
            identityDigest.Should().Be(BundleAcceptanceEvidenceDigestContract.Create(
                BundleAcceptanceEvidenceValueKinds.RecordRef,
                recordIdValue));
            identityDigest.Should().NotBe(BundleAcceptanceEvidenceDigestContract.Create(
                BundleAcceptanceEvidenceValueKinds.Integer,
                recordIdValue));

            var absentObservation = identityResult.AssertionObservations.Single(observation =>
                observation.AssertionId == "present_identity_still_uses_record_ref_digest");
            EvidenceMatches(
                    absentObservation,
                    BundleAcceptanceEvidenceValueKinds.Integer,
                    0,
                    1)
                .Should().BeTrue();
            absentObservation.ReportMetricDigests.Should().ContainSingle().Which
                .Should().Be(BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.RecordRef,
                    recordIdValue));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_report_value_refs_require_exact_compiler_identity_lineage()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_report_ref_kind_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-report-ref-kind",
                "accept-report-ref-kind.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty report-reference fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);

            var countSpoof = ExactReportTest(
                "count_cannot_spoof_record_identity",
                seedHash,
                [
                    ExactCreateJobStep("create_count_job", $"Count identity spoof {suffix}"),
                    ExactCallReportStep("call_job_count", "jobs_summary")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "count_is_not_record_ref",
                        AfterStep = "call_job_count",
                        Type = "report_metric_equals",
                        Report = "jobs_summary",
                        ReportRef = "call_job_count",
                        Metric = "total_jobs",
                        ValueRef = "create_count_job"
                    }
                ]);
            var wrongIdentityEntity = ExactReportTest(
                "identity_entity_must_match_value_ref",
                seedHash,
                [
                    ExactCreateJobStep("create_identity_job", $"Identity entity mismatch {suffix}"),
                    ExactCreateActivityStep("create_activity", "create_identity_job"),
                    ExactCallReportStep("call_activity_identity", "activity_by_job")
                ],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "job_identity_cannot_attest_activity_ref",
                        AfterStep = "call_activity_identity",
                        Type = "report_metric_equals",
                        Report = "activity_by_job",
                        ReportRef = "call_activity_identity",
                        Metric = "job",
                        ValueRef = "create_activity"
                    }
                ]);
            List<BundleAcceptanceTestDocument> tests = [countSpoof, wrongIdentityEntity];
            BundleAcceptanceTestContractHasher.Stamp(tests);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                tests);

            result.Status.Should().Be("failed");
            result.Passed.Should().Be(0);
            result.Failed.Should().Be(2);
            var results = result.Results.ToDictionary(test => test.Key, StringComparer.Ordinal);

            var countResult = results[countSpoof.Test.Key];
            countResult.StepObservations.Single(observation => observation.StepId == "create_count_job")
                .ProducedRecordId.Should().Be(1, "the report count and first record id are both one in the adversarial case");
            var countObservation = countResult.AssertionObservations.Should().ContainSingle().Which;
            countObservation.Status.Should().Be(RuntimeBundleTestAssertionObservationStatuses.ExecutionError);
            countObservation.Detail.Should().Be(
                $"{RuntimeBundleTestEvidenceFailureCodes.AssertionExecutionError}:count_is_not_record_ref");
            countObservation.ExpectedValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.RecordRef);
            countObservation.ExpectedValueDigest.Should().Be(
                BundleAcceptanceEvidenceDigestContract.Create(
                    BundleAcceptanceEvidenceValueKinds.RecordRef,
                    1L).Digest);
            countObservation.ActualValueDigest.Should().BeEmpty();
            countObservation.ReportMetricDigests.Should().BeEmpty();

            var entityResult = results[wrongIdentityEntity.Test.Key];
            var entityObservation = entityResult.AssertionObservations.Should().ContainSingle().Which;
            entityObservation.Status.Should().Be(RuntimeBundleTestAssertionObservationStatuses.ExecutionError);
            entityObservation.Detail.Should().Be(
                $"{RuntimeBundleTestEvidenceFailureCodes.AssertionExecutionError}:job_identity_cannot_attest_activity_ref");
            entityObservation.ExpectedValueKind.Should().Be(BundleAcceptanceEvidenceValueKinds.RecordRef);
            entityObservation.ActualValueDigest.Should().BeEmpty();
            entityObservation.ReportMetricDigests.Should().BeEmpty();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_record_exists_and_count_are_exact_beyond_the_record_list_page_limit()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_exact_count_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-exact-count",
                "accept-exact-count.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seededJobs = Enumerable.Range(0, 101)
                .Select(index => new BundleSeedRecord
                {
                    Ref = $"job_{index:D3}",
                    Entity = "job",
                    Values = new Dictionary<string, object?>
                    {
                        ["name"] = $"Exact count job {index:D3} {suffix}",
                        ["status"] = index == 0 ? "needle" : "ordinary"
                    }
                })
                .ToList();
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "many_jobs", Label = "More than one record page" },
                Records = seededJobs
            };
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "exact_counts_beyond_page_limit",
                    Label = "Exact record assertions scan the full typed relation"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = seed.Seed.Key,
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors = [new BundleAcceptanceActor { Id = "observer", Roles = ["operator"] }],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "all_jobs_count",
                        AfterStep = "$setup",
                        Type = "record_count",
                        Entity = "job",
                        Count = 101
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "needle_exists",
                        AfterStep = "$setup",
                        Type = "record_exists",
                        Entity = "job",
                        Where = new BundleAcceptanceWhere
                        {
                            Field = "status",
                            Op = "eq",
                            Value = "needle"
                        }
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "needle_count",
                        AfterStep = "$setup",
                        Type = "record_count",
                        Entity = "job",
                        Where = new BundleAcceptanceWhere
                        {
                            Field = "status",
                            Op = "eq",
                            Value = "needle"
                        },
                        Count = 1
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var observations = result.Results.Single().AssertionObservations
                .ToDictionary(observation => observation.AssertionId, StringComparer.Ordinal);
            EvidenceMatches(
                    observations["all_jobs_count"],
                    BundleAcceptanceEvidenceValueKinds.Integer,
                    101,
                    101)
                .Should().BeTrue();
            EvidenceMatches(
                    observations["needle_exists"],
                    BundleAcceptanceEvidenceValueKinds.String,
                    "at_least_one",
                    BundleAcceptanceEvidenceValueKinds.Integer,
                    1)
                .Should().BeTrue();
            EvidenceMatches(
                    observations["needle_count"],
                    BundleAcceptanceEvidenceValueKinds.Integer,
                    1,
                    1)
                .Should().BeTrue();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_exact_report_count_and_absence_fail_closed_when_report_output_is_truncated()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_report_limit_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-report-limit",
                "accept-report-limit.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var jobs = Enumerable.Range(0, 101)
                .Select(index => new BundleSeedRecord
                {
                    Ref = $"job_{index:D3}",
                    Entity = "job",
                    Values = new Dictionary<string, object?>
                    {
                        ["name"] = $"Report limit job {index:D3} {suffix}",
                        ["status"] = "draft"
                    }
                });
            var activities = Enumerable.Range(0, 101)
                .Select(index => new BundleSeedRecord
                {
                    Ref = $"activity_{index:D3}",
                    Entity = "activity_entry",
                    Values = new Dictionary<string, object?>
                    {
                        ["job"] = new Dictionary<string, object?> { ["ref"] = $"job_{index:D3}" },
                        ["message"] = $"Report limit activity {index:D3}"
                    }
                });
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "many_report_groups", Label = "More groups than the report limit" },
                Records = jobs.Concat(activities).ToList()
            };
            var test = ExactReportTest(
                "truncated_exact_report_proof",
                BundleSeedContractHasher.ComputeHash(seed),
                [ExactCallReportStep("call_activities", "activity_by_job")],
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "truncated_identity_metric",
                        AfterStep = "call_activities",
                        Type = "report_metric_equals",
                        Report = "activity_by_job",
                        ReportRef = "call_activities",
                        Metric = "job",
                        ValueRef = "job_000"
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "truncated_row_count",
                        AfterStep = "call_activities",
                        Type = "report_row_count",
                        Report = "activity_by_job",
                        ReportRef = "call_activities",
                        Count = 100
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "truncated_absence",
                        AfterStep = "call_activities",
                        Type = "report_record_absent",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "job_100" },
                        Report = "activity_by_job",
                        ReportRef = "call_activities",
                        Metric = "job"
                    }
                ],
                seedKey: seed.Seed.Key);
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("failed");
            var observations = result.Results.Single().AssertionObservations
                .ToDictionary(observation => observation.AssertionId, StringComparer.Ordinal);
            observations.Should().HaveCount(3);
            observations["truncated_identity_metric"].Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed &&
                observation.ReportTruncated && observation.ReportAppliedLimit == 100 &&
                observation.RowCount == 100);
            observations.Values.Where(observation => observation.AssertionId != "truncated_identity_metric")
                .Should().OnlyContain(observation =>
                observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                observation.ReportTruncated &&
                observation.ReportAppliedLimit == 100 &&
                observation.RowCount == 100);
            result.Results.Single().Failures.Should().OnlyContain(failure =>
                failure.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_refuses_a_reusable_staging_database_without_destroying_preexisting_app_or_audit_data()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_nonpristine_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-nonpristine",
                "accept-nonpristine.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var preexistingOwner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"preexisting-{suffix}@staging.test");
            var preexistingJob = await records.CreateRecordAsync(
                tenant,
                preexistingOwner,
                "job",
                RuntimeJson.JsonObject(
                    ("name", $"Preexisting staging job {suffix}"),
                    ("status", "draft")));
            await using (var linkConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await linkConnection.OpenAsync();
                await using var linkCommand = linkConnection.CreateCommand();
                linkCommand.CommandText = """
                    insert into _runtime.runtime_user_links (
                      runtime_user_id,
                      entity_key,
                      record_id,
                      created_by)
                    values (@runtimeUserId, 'job', @recordId, @runtimeUserId)
                    """;
                linkCommand.Parameters.AddWithValue("runtimeUserId", preexistingOwner.UserId);
                linkCommand.Parameters.AddWithValue("recordId", Convert.ToInt64(preexistingJob["id"]));
                await linkCommand.ExecuteNonQueryAsync();
            }
            await records.RecordAuditEventAsync(
                tenant,
                user: null,
                "preexisting.marker",
                "Must survive a refused acceptance run.",
                new Dictionary<string, object?>());
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty fixture" }
            };
            var test = EmptyV3Test(
                "must_not_claim_reusable_staging",
                seed.Seed.Key,
                BundleSeedContractHasher.ComputeHash(seed));
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var run = () => runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            await run.Should().ThrowAsync<RuntimeBundleTestException>()
                .WithMessage("*pristine app/integration tables*");
            var jobs = await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 100));
            jobs.Total.Should().Be(1);
            jobs.Records.Single()["name"].Should().Be($"Preexisting staging job {suffix}");
            (await records.ListAuditEventsAsync(tenant, 100)).Should().Contain(@event =>
                @event.EventType == "preexisting.marker");
            (await new RuntimeUserStore(factory).GetActiveUserContextAsync(tenant, preexistingOwner.UserId))
                .Should().NotBeNull();

            await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await connection.OpenAsync();
            await using var markerCommand = connection.CreateCommand();
            markerCommand.CommandText = "select to_regclass('_runtime.acceptance_test_ownership')::text";
            (await markerCommand.ExecuteScalarAsync()).Should().Be(DBNull.Value,
                "the failed ownership transaction must not leave a marker table or claim");
            await using var linkCountCommand = connection.CreateCommand();
            linkCountCommand.CommandText = "select count(*) from _runtime.runtime_user_links";
            Convert.ToInt64(await linkCountCommand.ExecuteScalarAsync()).Should().Be(1,
                "a refused run cannot delete preexisting runtime identity links");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Legacy_v2_acceptance_keeps_its_nonpristine_database_behavior()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_v2_nonpristine_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-v2-nonpristine",
                "accept-v2-nonpristine.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            await records.CreateRecordAsync(
                tenant,
                new RuntimeUserContext(42, "legacy@staging.test", "operator"),
                "job",
                RuntimeJson.JsonObject(
                    ("name", $"Legacy staging job {suffix}"),
                    ("status", "draft")));
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V2,
                Test = new BundleAcceptanceTest
                {
                    Key = "legacy_nonpristine_count",
                    Label = "Legacy v2 observes existing staging data"
                },
                Setup = new BundleAcceptanceSetup { Seed = "none" },
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Type = "record_count",
                        Entity = "job",
                        Count = 1
                    }
                ]
            };

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed: null,
                [test]);

            result.Status.Should().Be("passed");
            (await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 100)))
                .Total.Should().Be(1);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_cancellation_releases_the_lease_cleans_partial_data_and_restores_the_exact_seed()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_cancel_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-cancel",
                "accept-cancel.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "cancel_seed", Label = "Seed restored after cancellation" },
                Records = Enumerable.Range(0, 300)
                    .Select(index => new BundleSeedRecord
                    {
                        Ref = $"job_{index:D3}",
                        Entity = "job",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Cancellation job {index:D3} {suffix}",
                            ["status"] = "draft"
                        }
                    })
                    .ToList()
            };
            var test = EmptyV3Test(
                "cancel_during_seed",
                seed.Seed.Key,
                BundleSeedContractHasher.ComputeHash(seed));
            BundleAcceptanceTestContractHasher.Stamp([test]);
            using var cancellation = new CancellationTokenSource();
            await using var observer = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await observer.OpenAsync();

            var runTask = runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test],
                cancellation.Token);
            var markerObserved = false;
            for (var attempt = 0; attempt < 400 && !runTask.IsCompleted; attempt++)
            {
                await using var markerCommand = observer.CreateCommand();
                markerCommand.CommandText = "select to_regclass('_runtime.acceptance_test_ownership') is not null";
                if (await markerCommand.ExecuteScalarAsync() is true)
                {
                    markerObserved = true;
                    cancellation.Cancel();
                    break;
                }

                await Task.Delay(5);
            }

            markerObserved.Should().BeTrue("cancellation must exercise the acquired v3 ownership boundary");
            var awaitRun = async () => await runTask;
            await awaitRun.Should().ThrowAsync<OperationCanceledException>();

            (await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 1)))
                .Total.Should().Be(300, "the outer finally restores the exact bound seed after cleanup");
            await using var releasedCommand = observer.CreateCommand();
            releasedCommand.CommandText = "select count(*) from _runtime.acceptance_test_ownership";
            Convert.ToInt64(await releasedCommand.ExecuteScalarAsync()).Should().Be(0);
            var audit = await records.ListAuditEventsAsync(tenant, 200);
            audit.Count(@event => @event.EventType == "seed.applied").Should().Be(1);
            audit.Should().NotContain(@event => @event.EventType == "bundle_tests.completed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Concurrent_v3_run_is_refused_without_mutating_the_active_lease_or_first_run()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_concurrent_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-concurrent",
                "accept-concurrent.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            RuntimeBundleTestRunner CreateRunner() => new(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "concurrent_seed", Label = "Keeps the first lease active" },
                Records = Enumerable.Range(0, 800)
                    .Select(index => new BundleSeedRecord
                    {
                        Ref = $"job_{index:D3}",
                        Entity = "job",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Concurrent job {index:D3} {suffix}",
                            ["status"] = "draft"
                        }
                    })
                    .ToList()
            };
            var test = EmptyV3Test(
                "exclusive_v3_lease",
                seed.Seed.Key,
                BundleSeedContractHasher.ComputeHash(seed));
            BundleAcceptanceTestContractHasher.Stamp([test]);
            await using var observer = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await observer.OpenAsync();

            var firstRun = CreateRunner().RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);
            Guid? activeRunId = null;
            for (var attempt = 0; attempt < 400 && !firstRun.IsCompleted; attempt++)
            {
                await using var markerCommand = observer.CreateCommand();
                markerCommand.CommandText = """
                    select run_id
                    from _runtime.acceptance_test_ownership
                    where id = true
                    """;
                try
                {
                    var value = await markerCommand.ExecuteScalarAsync();
                    if (value is Guid runId)
                    {
                        activeRunId = runId;
                        break;
                    }
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
                {
                    // The ownership table is created atomically with the first marker.
                }

                await Task.Delay(5);
            }

            activeRunId.Should().NotBeNull("the first v3 runner must own the database before the adversarial call");
            var secondRun = () => CreateRunner().RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);
            await secondRun.Should().ThrowAsync<RuntimeBundleTestException>()
                .WithMessage("*already leased by another test run*");

            await using (var markerCommand = observer.CreateCommand())
            {
                markerCommand.CommandText = "select run_id from _runtime.acceptance_test_ownership where id = true";
                (await markerCommand.ExecuteScalarAsync()).Should().Be(activeRunId.Value,
                    "the refused runner cannot replace or clear the active marker");
            }

            var firstResult = await firstRun;
            firstResult.Status.Should().Be("passed");
            (await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 1)))
                .Total.Should().Be(800);
            await using var releasedCommand = observer.CreateCommand();
            releasedCommand.CommandText = "select count(*) from _runtime.acceptance_test_ownership";
            Convert.ToInt64(await releasedCommand.ExecuteScalarAsync()).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_result_persistence_failure_cleans_and_restores_seed_without_false_passing_evidence()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_store_fail_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-store-fail",
                "accept-store-fail.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            await using (var setupConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await setupConnection.OpenAsync();
                await RuntimeBundleTestRunner.EnsureTestResultsTableAsync(setupConnection, CancellationToken.None);
                await using var rejectPersistence = setupConnection.CreateCommand();
                rejectPersistence.CommandText = """
                    alter table _runtime.test_results
                    add constraint reject_acceptance_result_insert check (false)
                    """;
                await rejectPersistence.ExecuteNonQueryAsync();
            }

            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "store_failure_seed", Label = "Seed restored after store failure" },
                Records =
                [
                    new BundleSeedRecord
                    {
                        Ref = "baseline_job",
                        Entity = "job",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Store failure baseline {suffix}",
                            ["status"] = "draft"
                        }
                    }
                ]
            };
            var test = EmptyV3Test(
                "store_result_failure",
                seed.Seed.Key,
                BundleSeedContractHasher.ComputeHash(seed));
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var run = () => runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            await run.Should().ThrowAsync<PostgresException>()
                .Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);
            var jobs = await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 10));
            jobs.Total.Should().Be(1);
            jobs.Records.Single()["name"].Should().Be($"Store failure baseline {suffix}");
            var audit = await records.ListAuditEventsAsync(tenant, 100);
            audit.Count(@event => @event.EventType == "seed.applied").Should().Be(1);
            audit.Should().NotContain(@event => @event.EventType == "bundle_tests.completed");

            await using var verifyConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = """
                select
                  (select count(*) from _runtime.acceptance_test_ownership),
                  (select count(*) from _runtime.test_results)
                """;
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(0);
            reader.GetInt64(1).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_completion_audit_failure_never_persists_a_result_and_releases_test_ownership()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_audit_fail_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-audit-fail",
                "accept-audit-fail.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            await using (var setupConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await setupConnection.OpenAsync();
                await using var rejectCompletionAudit = setupConnection.CreateCommand();
                rejectCompletionAudit.CommandText = """
                    alter table audit.events
                    add constraint reject_bundle_test_completion_audit
                    check (event_type <> 'bundle_tests.completed')
                    """;
                await rejectCompletionAudit.ExecuteNonQueryAsync();
            }

            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty audit failure fixture" }
            };
            var test = EmptyV3Test(
                "completion_audit_failure",
                seed.Seed.Key,
                BundleSeedContractHasher.ComputeHash(seed));
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var run = () => runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            await run.Should().ThrowAsync<PostgresException>()
                .Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);
            await using var verifyConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = """
                select
                  (select count(*) from _runtime.acceptance_test_ownership),
                  (select count(*) from _runtime.test_results),
                  (select count(*) from audit.events where event_type = 'bundle_tests.completed')
                """;
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(0);
            reader.GetInt64(1).Should().Be(0);
            reader.GetInt64(2).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_direct_rerun_requires_a_fresh_staging_reset_after_durable_evidence()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_rerun_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-rerun",
                "accept-rerun.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty one-shot fixture" }
            };
            var test = EmptyV3Test(
                "one_shot_v3_suite",
                seed.Seed.Key,
                BundleSeedContractHasher.ComputeHash(seed));
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var first = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);
            var rerun = () => runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            first.Status.Should().Be("passed");
            (await runner.HasPassingResultAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                first.TestSuiteHash)).Should().BeTrue();
            await rerun.Should().ThrowAsync<RuntimeBundleTestException>()
                .WithMessage("*pristine test-owned staging database*");
            (await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 1)))
                .Total.Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_seed_key_alias_is_rejected_before_the_hash_matching_seed_is_applied()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_seed_key_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-seed-key",
                "accept-seed-key.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "exact_seed", Label = "Exact seed" },
                Records =
                [
                    new BundleSeedRecord
                    {
                        Ref = "seeded_job",
                        Entity = "job",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Must not be applied {suffix}",
                            ["status"] = "draft"
                        }
                    }
                ]
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var exact = EmptyV3Test("exact_seed_consumer", "exact_seed", seedHash);
            var alias = EmptyV3Test("aliased_seed_consumer", "EXACT_SEED", seedHash);
            BundleAcceptanceTestContractHasher.Stamp([exact, alias]);

            var run = () => runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [exact, alias]);

            await run.Should().ThrowAsync<RuntimeBundleTestException>()
                .WithMessage("*exact hash-bound seed key and contract*");
            var jobs = await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 100));
            jobs.Total.Should().Be(0);
            (await records.ListAuditEventsAsync(tenant, 100)).Should().NotContain(@event =>
                @event.EventType == "seed.applied");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_action_evidence_uses_the_created_entity_id_instead_of_inferring_it_from_the_key()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_entity_id_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-entity-id",
                "accept-entity-id.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty exact fixture" }
            };
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "irregular_created_entity_id",
                    Label = "Action result retains an irregular created entity id"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = "none",
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors = [new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] }],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        AsActor = "operator_actor",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Irregular entity job {suffix}",
                            ["status"] = "draft"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "create_activity",
                        Type = "run_action",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Action = "create_activity_entry",
                        AsRole = "operator",
                        AsActor = "operator_actor"
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "created_activity_message",
                        AfterStep = "create_activity",
                        Type = "record_field_equals",
                        Entity = "activity_entry",
                        Record = new BundleRecordSelector { Ref = "create_activity" },
                        Field = "message",
                        Value = "created by action"
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            runtimeTest.StepObservations.Single(observation => observation.StepId == "create_activity")
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.ProducedEntity == "activity_entry" &&
                    observation.ProducedRecordRef == "create_activity" &&
                    observation.ProducedRecordId != null);
            runtimeTest.AssertionObservations.Should().ContainSingle(observation =>
                observation.AssertionId == "created_activity_message" &&
                EvidenceMatches(
                    observation,
                    BundleAcceptanceEvidenceValueKinds.String,
                    "created by action",
                    "created by action"));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_actor_roles_must_exist_in_active_definitions_before_lease_or_seed_mutation()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_actor_roles_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-actor-roles",
                "accept-actor-roles.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty exact fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var unknownRole = NegativeCreatePermissionTest("not_emitted", seedHash);
            BundleAcceptanceTestContractHasher.Stamp([unknownRole]);
            var auditBefore = await records.ListAuditEventsAsync(tenant, 100);

            var runUnknown = () => runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [unknownRole]);

            await runUnknown.Should().ThrowAsync<RuntimeBundleTestException>()
                .WithMessage("*not emitted by the compiled bundle*");
            (await records.ListAuditEventsAsync(tenant, 100)).Should().HaveCount(auditBefore.Count);
            await using (var connection = new NpgsqlConnection(
                             factory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning)))
            {
                await connection.OpenAsync();
                await using var ownership = connection.CreateCommand();
                ownership.CommandText = "select to_regclass('_runtime.acceptance_test_ownership') is not null";
                (await ownership.ExecuteScalarAsync()).Should().Be(false);

                await using var role = connection.CreateCommand();
                role.CommandText = """
                    insert into _runtime.role_definitions (id, display_name, grants, updated_at)
                    values ('no_grants', 'No grants', '[]'::jsonb, now());

                    insert into _runtime.runtime_roles (id, display_name, updated_at)
                    values ('no_grants', 'No grants', now())
                    """;
                await role.ExecuteNonQueryAsync();
            }

            var declaredWithoutGrants = NegativeCreatePermissionTest("no_grants", seedHash);
            BundleAcceptanceTestContractHasher.Stamp([declaredWithoutGrants]);
            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [declaredWithoutGrants]);

            result.Status.Should().Be("passed");
            result.Results.Should().ContainSingle().Which.StepObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                    observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.PermissionDenied);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_failed_workflow_status_assertion_retains_the_exact_instances_actual_status()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_workflow_evidence_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-workflow-evidence",
                "accept-workflow-evidence.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty exact fixture" }
            };
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "failed_workflow_evidence",
                    Label = "Failed workflow assertion retains actual state"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = "none",
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors = [new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] }],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        AsActor = "operator_actor",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Workflow evidence job {suffix}",
                            ["status"] = "draft"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "start_approval",
                        Type = "start_workflow_action",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Action = "submit_for_approval",
                        AsRole = "operator",
                        AsActor = "operator_actor"
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "incorrect_completed_expectation",
                        AfterStep = "start_approval",
                        Type = "workflow_status",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Workflow = "job_approval",
                        WorkflowRef = "start_approval",
                        Status = "completed"
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("failed");
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            var start = runtimeTest.StepObservations.Single(observation => observation.StepId == "start_approval");
            runtimeTest.AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Failed &&
                    observation.WorkflowInstanceId == start.WorkflowInstanceId &&
                    observation.WorkflowStatus == "waiting" &&
                    EvidenceMatches(
                        observation,
                        BundleAcceptanceEvidenceValueKinds.String,
                        "completed",
                        "waiting"));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Legacy_v2_workflow_status_uses_any_matching_instance_when_history_contains_duplicates()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_v2_workflow_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-v2-workflow",
                "accept-v2-workflow.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                workflows,
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty legacy fixture" }
            };
            var test = new BundleAcceptanceTestDocument
            {
                Test = new BundleAcceptanceTest
                {
                    Key = "legacy_duplicate_workflow_history",
                    Label = "Legacy assertion accepts any matching workflow instance"
                },
                Setup = new BundleAcceptanceSetup { Seed = "none" },
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Legacy duplicate workflow job {suffix}",
                            ["status"] = "draft"
                        }
                    },
                    StartWorkflowStep("start_first", "create_job"),
                    RejectWorkflowStep("reject_first", "create_job", "start_first"),
                    StartWorkflowStep("start_second", "create_job"),
                    RejectWorkflowStep("reject_second", "create_job", "start_second")
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Type = "workflow_status",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_job" },
                        Workflow = "job_approval",
                        Status = "rejected"
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            runtimeTest.StepObservations.Where(observation => observation.StepType == "start_workflow_action")
                .Select(observation => observation.WorkflowInstanceId)
                .Should().OnlyHaveUniqueItems().And.HaveCount(2);
            runtimeTest.AssertionObservations.Should().ContainSingle().Which
                .Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed &&
                    observation.WorkflowStatus == "rejected" &&
                    Equals(observation.ExpectedValue, "rejected") &&
                    Equals(observation.ActualValue, "rejected") &&
                    string.IsNullOrEmpty(observation.ExpectedValueDigestSchema) &&
                    string.IsNullOrEmpty(observation.ExpectedValueKind) &&
                    string.IsNullOrEmpty(observation.ExpectedValueDigest) &&
                    string.IsNullOrEmpty(observation.ActualValueDigestSchema) &&
                    string.IsNullOrEmpty(observation.ActualValueKind) &&
                    string.IsNullOrEmpty(observation.ActualValueDigest));
            (await workflows.ListInstancesAsync(
                    tenant,
                    new RuntimeUserContext(-1, "owner@tests.invalid", RuntimeSystemRoleIds.TenantOwner)
                    {
                        Roles = [RuntimeSystemRoleIds.TenantOwner]
                    }))
                .Count(instance => instance.WorkflowId == "job_approval").Should().Be(2);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_contracts_reset_records_workflows_and_audits_before_and_after_every_test()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_v3_isolation_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-v3-isolation",
                "accept-v3-isolation.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                workflows,
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty isolated fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var first = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "first_isolated_contract",
                    Label = "First contract creates mutable side effects"
                },
                Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
                Actors = [new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] }],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_first_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        AsActor = "operator_actor",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"First isolated job {suffix}",
                            ["status"] = "draft"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "start_first_workflow",
                        Type = "start_workflow_action",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_first_job" },
                        Action = "submit_for_approval",
                        AsRole = "operator",
                        AsActor = "operator_actor"
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "first_record_audited",
                        AfterStep = "create_first_job",
                        Type = "audit_event_exists",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_first_job" },
                        Event = "record.created"
                    }
                ]
            };
            var second = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "second_isolated_contract",
                    Label = "Second contract starts from only its exact seed"
                },
                Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
                Actors = [new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] }],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_second_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        AsActor = "operator_actor",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Second isolated job {suffix}",
                            ["status"] = "draft"
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "start_second_workflow",
                        Type = "start_workflow_action",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_second_job" },
                        Action = "submit_for_approval",
                        AsRole = "operator",
                        AsActor = "operator_actor"
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "no_record_leaked_from_first_contract",
                        AfterStep = BundleAcceptanceAssertionCheckpoints.Setup,
                        Type = "record_count",
                        Entity = "job",
                        Count = 0
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "only_second_record_exists",
                        AfterStep = "create_second_job",
                        Type = "record_count",
                        Entity = "job",
                        Count = 1
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "second_workflow_is_exact",
                        AfterStep = "start_second_workflow",
                        Type = "workflow_status",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_second_job" },
                        Workflow = "job_approval",
                        WorkflowRef = "start_second_workflow",
                        Status = "waiting"
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "second_record_audit_is_fresh",
                        AfterStep = "create_second_job",
                        Type = "audit_event_exists",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_second_job" },
                        Event = "record.created"
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([first, second]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [first, second]);

            result.Status.Should().Be("passed");
            result.Results.Should().HaveCount(2).And.OnlyContain(test => test.Status == "passed");
            EvidenceMatches(
                    result.Results.Single(test => test.Key == "second_isolated_contract")
                        .AssertionObservations.Single(observation =>
                            observation.AssertionId == "no_record_leaked_from_first_contract"),
                    BundleAcceptanceEvidenceValueKinds.Integer,
                    0,
                    0)
                .Should().BeTrue();
            (await records.ListRecordsAsync(tenant, "job", new RuntimeRecordQuery(PageSize: 100)))
                .Total.Should().Be(0);
            (await workflows.ListInstancesAsync(
                    tenant,
                    new RuntimeUserContext(-1, "owner@tests.invalid", RuntimeSystemRoleIds.TenantOwner)
                    {
                        Roles = [RuntimeSystemRoleIds.TenantOwner]
                    }))
                .Should().BeEmpty();
            var remainingAudit = await records.ListAuditEventsAsync(tenant, 100);
            remainingAudit.Should().ContainSingle(@event => @event.EventType == "bundle_tests.completed");
            remainingAudit.Should().NotContain(@event =>
                @event.EventType == "record.created" ||
                @event.EventType == "workflow.started" ||
                @event.EventType == "seed.applied");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_actor_principal_tokens_resolve_to_the_exact_owner_and_assignee_principals()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_actor_value_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-actor-value",
                "accept-actor-value.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateExpectedOutcomeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty actor fixture" }
            };
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "exact_actor_principal_values",
                    Label = "Actor value tokens bind owner and assignee principals"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = "none",
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors =
                [
                    new BundleAcceptanceActor { Id = "owner_actor", Roles = ["operator"] },
                    new BundleAcceptanceActor { Id = "assignee_actor", Roles = ["operator"] }
                ],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_owned_job",
                        Type = "create_record",
                        Entity = "job",
                        AsRole = "operator",
                        AsActor = "owner_actor",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = $"Actor-owned job {suffix}",
                            ["status"] = "draft",
                            ["owner_principal"] = BundleAcceptanceActorPrincipalToken.For("owner_actor"),
                            ["assignee_principal"] = BundleAcceptanceActorPrincipalToken.For("assignee_actor")
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "confirm_assignment",
                        Type = "update_record",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_owned_job" },
                        AsRole = "operator",
                        AsActor = "assignee_actor",
                        Set = new Dictionary<string, object?>
                        {
                            ["assignee_principal"] = BundleAcceptanceActorPrincipalToken.For("assignee_actor")
                        }
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "owner_matches_actor",
                        AfterStep = "confirm_assignment",
                        Type = "record_field_equals",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_owned_job" },
                        Field = "owner_principal",
                        Value = BundleAcceptanceActorPrincipalToken.For("owner_actor")
                    },
                    new BundleAcceptanceAssertion
                    {
                        Id = "assignee_matches_actor",
                        AfterStep = "confirm_assignment",
                        Type = "record_field_equals",
                        Entity = "job",
                        Record = new BundleRecordSelector { Ref = "create_owned_job" },
                        Field = "assignee_principal",
                        Value = BundleAcceptanceActorPrincipalToken.For("assignee_actor")
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            var ownerPrincipal = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "create_owned_job").PrincipalId;
            var assigneePrincipal = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "confirm_assignment").PrincipalId;
            ownerPrincipal.Should().NotBeNull();
            assigneePrincipal.Should().NotBeNull().And.NotBe(ownerPrincipal);
            var ownerEvidence = runtimeTest.AssertionObservations.Single(observation =>
                observation.AssertionId == "owner_matches_actor");
            var assigneeEvidence = runtimeTest.AssertionObservations.Single(observation =>
                observation.AssertionId == "assignee_matches_actor");
            EvidenceMatches(
                    ownerEvidence,
                    BundleAcceptanceEvidenceValueKinds.ActorPrincipal,
                    ownerPrincipal,
                    ownerPrincipal)
                .Should().BeTrue();
            EvidenceMatches(
                    assigneeEvidence,
                    BundleAcceptanceEvidenceValueKinds.ActorPrincipal,
                    assigneePrincipal,
                    assigneePrincipal)
                .Should().BeTrue();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static bool EvidenceMatches(
        RuntimeBundleTestAssertionObservation observation,
        string kind,
        object? expected,
        object? actual) =>
        EvidenceMatches(observation, kind, expected, kind, actual);

    private static bool EvidenceMatches(
        RuntimeBundleTestAssertionObservation observation,
        string expectedKind,
        object? expected,
        string actualKind,
        object? actual)
    {
        var expectedEvidence = BundleAcceptanceEvidenceDigestContract.Create(expectedKind, expected);
        var actualEvidence = BundleAcceptanceEvidenceDigestContract.Create(actualKind, actual);
        return observation.ExpectedValue is null &&
               observation.ActualValue is null &&
               observation.ExpectedValueDigestSchema == expectedEvidence.Schema &&
               observation.ExpectedValueKind == expectedEvidence.Kind &&
               observation.ExpectedValueDigest == expectedEvidence.Digest &&
               observation.ActualValueDigestSchema == actualEvidence.Schema &&
               observation.ActualValueKind == actualEvidence.Kind &&
               observation.ActualValueDigest == actualEvidence.Digest;
    }

    private static BundleAcceptanceTestDocument ExactReportTest(
        string key,
        string seedHash,
        IReadOnlyList<BundleAcceptanceStep> steps,
        IReadOnlyList<BundleAcceptanceAssertion> assertions,
        string seedKey = "none") => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest { Key = key, Label = key },
        Setup = new BundleAcceptanceSetup { Seed = seedKey, SeedHash = seedHash },
        Actors =
        [
            new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] },
            new BundleAcceptanceActor { Id = "restricted_actor", Roles = ["restricted"] }
        ],
        Steps = steps.ToList(),
        Assertions = assertions.ToList()
    };

    private static BundleAcceptanceStep ExactCreateJobStep(string id, string name) => new()
    {
        Id = id,
        Type = "create_record",
        Entity = "job",
        AsRole = "operator",
        AsActor = "operator_actor",
        Values = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["status"] = "draft"
        }
    };

    private static BundleAcceptanceStep ExactCreateActivityStep(string id, string recordRef) => new()
    {
        Id = id,
        Type = "run_action",
        Entity = "job",
        Record = new BundleRecordSelector { Ref = recordRef },
        Action = "create_activity_entry",
        AsRole = "operator",
        AsActor = "operator_actor"
    };

    private static BundleAcceptanceStep ExactCallReportStep(
        string id,
        string report,
        string actor = "operator_actor",
        string role = "operator") => new()
    {
        Id = id,
        Type = "call_report",
        Report = report,
        AsRole = role,
        AsActor = actor
    };

    private static BundleAcceptanceTestDocument EmptyV3Test(string key, string seedKey, string seedHash) => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest { Key = key, Label = key },
        Setup = new BundleAcceptanceSetup { Seed = seedKey, SeedHash = seedHash }
    };

    private static BundleAcceptanceTestDocument NegativeCreatePermissionTest(string role, string seedHash) => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest
        {
            Key = $"negative_create_{role}",
            Label = $"Role {role} is denied create"
        },
        Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
        Actors =
        [
            new BundleAcceptanceActor { Id = "restricted_actor", Roles = [role] }
        ],
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "attempt_create",
                Type = "create_record",
                Entity = "job",
                AsActor = "restricted_actor",
                AsRole = role,
                Values = new Dictionary<string, object?>
                {
                    ["name"] = "Denied job",
                    ["status"] = "draft"
                },
                Expected = new BundleAcceptanceExpectedOutcome
                {
                    Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    Code = BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate,
                    Target = "job"
                }
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "job_not_created",
                AfterStep = "attempt_create",
                Type = "record_count",
                Entity = "job",
                Count = 0
            }
        ]
    };

    private static BundleAcceptanceStep StartWorkflowStep(string id, string recordRef) => new()
    {
        Id = id,
        Type = "start_workflow_action",
        Entity = "job",
        Record = new BundleRecordSelector { Ref = recordRef },
        Action = "submit_for_approval",
        AsRole = "operator"
    };

    private static BundleAcceptanceStep RejectWorkflowStep(string id, string recordRef, string workflowRef) => new()
    {
        Id = id,
        Type = "reject_task",
        Entity = "job",
        Record = new BundleRecordSelector { Ref = recordRef },
        WorkflowRef = workflowRef,
        AsRole = "manager"
    };

    [PostgresFact]
    public async Task V3_expected_unassigned_approval_denial_uses_exact_task_and_preserves_positive_control()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_task_assignment_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-task-assignment",
                "accept-task-assignment.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var bundle = CreateWorkflowTaskPermissionBundle();
            var compiled = new ErpBundleCompiler().Compile(bundle);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                workflows,
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty exact workflow permission fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var test = WorkflowTaskPermissionDocument(
                suffix,
                seedHash,
                deniedActorId: "reviewer_actor",
                deniedRole: "reviewer",
                deniedCode: BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                deniedTarget: "manager_approval",
                includePositiveControl: true);
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            var subjectStart = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "start_subject_approval");
            var decoyStart = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "start_decoy_approval");
            var denied = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "reviewer_approval_denied");
            var approved = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "manager_approval_succeeds");

            subjectStart.WorkflowInstanceId.Should().BePositive().And.NotBe(decoyStart.WorkflowInstanceId);
            subjectStart.TaskId.Should().BePositive().And.NotBe(decoyStart.TaskId);
            subjectStart.TaskAssignedRole.Should().Be("manager");
            subjectStart.TaskAssignedUserId.Should().BeNull();
            decoyStart.TaskAssignedRole.Should().Be("manager");
            decoyStart.TaskAssignedUserId.Should().BeNull();
            runtimeTest.StepObservations.Single(observation => observation.StepId == "create_subject_job")
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.TaskAssignedUserId == null &&
                    string.IsNullOrEmpty(observation.TaskAssignedRole));
            denied.Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.PermissionDenied &&
                observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment &&
                observation.ActualTarget == "manager_approval" &&
                observation.ActorId == "reviewer_actor" &&
                observation.RecordRef == "create_subject_job" &&
                observation.RecordId == subjectStart.RecordId &&
                observation.WorkflowRef == "start_subject_approval" &&
                observation.WorkflowInstanceId == subjectStart.WorkflowInstanceId &&
                observation.Workflow == subjectStart.Workflow &&
                observation.TaskId == subjectStart.TaskId &&
                observation.WorkflowStep == subjectStart.WorkflowStep &&
                observation.TaskAssignedUserId == null &&
                observation.TaskAssignedRole == "manager");
            denied.WorkflowInstanceId.Should().NotBe(decoyStart.WorkflowInstanceId);
            denied.TaskId.Should().NotBe(decoyStart.TaskId);
            approved.Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.Success &&
                observation.ActorId == "manager_actor" &&
                observation.WorkflowRef == "start_subject_approval" &&
                observation.WorkflowInstanceId == subjectStart.WorkflowInstanceId &&
                observation.TaskId == subjectStart.TaskId &&
                observation.WorkflowStep == "manager_approval" &&
                observation.TaskAssignedUserId == null &&
                observation.TaskAssignedRole == "manager");

            var assertions = runtimeTest.AssertionObservations.ToDictionary(
                observation => observation.AssertionId,
                StringComparer.Ordinal);
            EvidenceMatches(
                    assertions["subject_unchanged_after_denial"],
                    BundleAcceptanceEvidenceValueKinds.String,
                    "open",
                    "open")
                .Should().BeTrue();
            assertions["subject_waiting_after_denial"].WorkflowStatus.Should().Be("waiting");
            assertions["subject_task_pending_after_denial"].TaskIds.Should().Equal(subjectStart.TaskId!.Value);
            assertions["subject_task_pending_after_denial"].TaskStatuses.Should().Equal("pending");
            EvidenceMatches(
                    assertions["subject_approved_by_manager"],
                    BundleAcceptanceEvidenceValueKinds.String,
                    "approved",
                    "approved")
                .Should().BeTrue();
            assertions["subject_completed_by_manager"].WorkflowStatus.Should().Be("completed");
            assertions["decoy_still_waiting"].WorkflowInstanceId.Should().Be(decoyStart.WorkflowInstanceId);
            assertions["decoy_still_waiting"].WorkflowStatus.Should().Be("waiting");
            assertions.Values.Should().OnlyContain(observation =>
                observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_relation_user_task_observations_bind_exact_user_and_reject_decoy_assignment_lineage()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_relation_assignment_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-relation-assignment",
                "accept-relation-assignment.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = new ErpBundleCompiler().Compile(CreateWorkflowTaskPermissionBundle(includeRelationWorkflow: true));
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty relation-user assignment fixture" }
            };
            var test = RelationUserTaskPermissionDocument(
                suffix,
                BundleSeedContractHasher.ComputeHash(seed));
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            var runtimeTest = result.Results.Should().ContainSingle().Which;
            var subjectManagerBind = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "bind_subject_manager");
            var decoyManagerBind = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "bind_decoy_manager");
            var subjectStart = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "start_subject_relation_approval");
            var decoyStart = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "start_decoy_relation_approval");
            var denied = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "reviewer_relation_approval_denied");
            var success = runtimeTest.StepObservations.Single(observation =>
                observation.StepId == "subject_manager_approval_succeeds");

            subjectManagerBind.PrincipalId.Should().BeNegative();
            decoyManagerBind.PrincipalId.Should().BeNegative().And.NotBe(subjectManagerBind.PrincipalId);
            subjectStart.Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.Workflow == "job_relation_approval" &&
                observation.WorkflowStep == "linked_manager_approval" &&
                observation.TaskAssignedUserId == subjectManagerBind.PrincipalId &&
                string.IsNullOrEmpty(observation.TaskAssignedRole));
            decoyStart.Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.WorkflowInstanceId != subjectStart.WorkflowInstanceId &&
                observation.TaskId != subjectStart.TaskId &&
                observation.TaskAssignedUserId == decoyManagerBind.PrincipalId &&
                observation.TaskAssignedUserId != subjectStart.TaskAssignedUserId &&
                string.IsNullOrEmpty(observation.TaskAssignedRole));
            denied.Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment &&
                observation.WorkflowRef == "start_subject_relation_approval" &&
                observation.WorkflowInstanceId == subjectStart.WorkflowInstanceId &&
                observation.TaskId == subjectStart.TaskId &&
                observation.TaskAssignedUserId == subjectManagerBind.PrincipalId &&
                observation.TaskAssignedUserId != observation.PrincipalId &&
                string.IsNullOrEmpty(observation.TaskAssignedRole));
            success.Should().Match<RuntimeBundleTestStepObservation>(observation =>
                observation.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.Success &&
                observation.ActorId == "subject_manager_actor" &&
                observation.PrincipalId == subjectManagerBind.PrincipalId &&
                observation.WorkflowInstanceId == subjectStart.WorkflowInstanceId &&
                observation.TaskId == subjectStart.TaskId &&
                observation.TaskAssignedUserId == subjectManagerBind.PrincipalId &&
                string.IsNullOrEmpty(observation.TaskAssignedRole));
            runtimeTest.StepObservations.Single(observation => observation.StepId == "create_subject_job")
                .Should().Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.TaskAssignedUserId == null && string.IsNullOrEmpty(observation.TaskAssignedRole));
            runtimeTest.AssertionObservations.Should().OnlyContain(observation =>
                observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_workflow_task_assignment_denial_fails_closed_for_wrong_code_target_success_scope_and_live_state()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_accept_task_assignment_bad_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-task-assignment-bad",
                "accept-task-assignment-bad.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var bundle = CreateWorkflowTaskPermissionBundle();
            bundle.Roles["scoped_reviewer"] = new ErpRole
            {
                DisplayName = "Scoped reviewer",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "job" },
                    new ErpGrant { Action = "read_task", Entity = "job" },
                    new ErpGrant
                    {
                        Action = "approve_task",
                        Entity = "job",
                        Scope = PermissionGrantScopes.Record,
                        RecordScope = new ErpRecordScope
                        {
                            Field = "owner_principal",
                            Source = RecordPermissionScopeSources.ActorPrincipal
                        }
                    }
                ]
            };
            bundle.Roles["read_only_reviewer"] = new ErpRole
            {
                DisplayName = "Read-only reviewer",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "job" },
                    new ErpGrant { Action = "read_task", Entity = "job" }
                ]
            };
            var compiled = new ErpBundleCompiler().Compile(bundle);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                new RuntimeReportStore(factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "none", Label = "Empty exact workflow permission fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);

            var wrongCode = WorkflowTaskPermissionDocument(
                $"{suffix}a",
                seedHash,
                "reviewer_actor",
                "reviewer",
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask,
                "manager_approval",
                includePositiveControl: false);
            var wrongTarget = WorkflowTaskPermissionDocument(
                $"{suffix}b",
                seedHash,
                "reviewer_actor",
                "reviewer",
                BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                "finance_approval",
                includePositiveControl: false);
            var unexpectedSuccess = WorkflowTaskPermissionDocument(
                $"{suffix}c",
                seedHash,
                "manager_actor",
                "manager",
                BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                "manager_approval",
                includePositiveControl: false);
            var confoundedScope = WorkflowTaskPermissionDocument(
                $"{suffix}d",
                seedHash,
                "scoped_reviewer_actor",
                "scoped_reviewer",
                BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                "manager_approval",
                includePositiveControl: false);
            confoundedScope.Actors.Add(new BundleAcceptanceActor
            {
                Id = "scoped_reviewer_actor",
                Roles = ["scoped_reviewer"]
            });
            var confoundedActionAndAssignment = WorkflowTaskPermissionDocument(
                $"{suffix}e",
                seedHash,
                "read_only_reviewer_actor",
                "read_only_reviewer",
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask,
                "manager_approval",
                includePositiveControl: false);
            confoundedActionAndAssignment.Actors.Add(new BundleAcceptanceActor
            {
                Id = "read_only_reviewer_actor",
                Roles = ["read_only_reviewer"]
            });
            var staleCompletedTask = WorkflowTaskPermissionDocument(
                $"{suffix}f",
                seedHash,
                "reviewer_actor",
                "reviewer",
                BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                "manager_approval",
                includePositiveControl: true);
            var staleDeniedStep = staleCompletedTask.Steps.Single(step =>
                step.Id == "reviewer_approval_denied");
            staleCompletedTask.Steps.Remove(staleDeniedStep);
            staleCompletedTask.Steps.Add(staleDeniedStep);
            staleCompletedTask.Assertions.Clear();
            BundleAcceptanceTestContractHasher.Stamp(
                [wrongCode, wrongTarget, unexpectedSuccess, confoundedScope, confoundedActionAndAssignment, staleCompletedTask]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [wrongCode, wrongTarget, unexpectedSuccess, confoundedScope, confoundedActionAndAssignment, staleCompletedTask]);

            result.Status.Should().Be("failed");
            result.Results.Should().HaveCount(6).And.OnlyContain(test => test.Status == "failed");
            result.Results[0].StepObservations.Last().Should()
                .Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Failed &&
                    observation.ActualCode == BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment);
            result.Results[1].StepObservations.Last().Should()
                .Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.ExecutionError &&
                    observation.ActualOutcome == RuntimeBundleTestActualOutcomes.ExecutionError);
            result.Results[2].StepObservations.Last().Should()
                .Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.Status == RuntimeBundleTestStepObservationStatuses.Failed &&
                    observation.ActualOutcome == BundleAcceptanceExpectedOutcomes.Success);
            result.Results[3].StepObservations.Last().Should()
                .Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.StepId == "reviewer_approval_denied" &&
                    observation.Status == RuntimeBundleTestStepObservationStatuses.ExecutionError &&
                    observation.ActualOutcome == RuntimeBundleTestActualOutcomes.ExecutionError &&
                    string.IsNullOrEmpty(observation.ActualCode));
            result.Results[4].StepObservations.Last().Should()
                .Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.StepId == "reviewer_approval_denied" &&
                    observation.Status == RuntimeBundleTestStepObservationStatuses.ExecutionError &&
                    observation.ActualOutcome == RuntimeBundleTestActualOutcomes.ExecutionError &&
                    string.IsNullOrEmpty(observation.ActualCode));
            result.Results[5].StepObservations.Last().Should()
                .Match<RuntimeBundleTestStepObservation>(observation =>
                    observation.StepId == "reviewer_approval_denied" &&
                    observation.Status == RuntimeBundleTestStepObservationStatuses.ExecutionError &&
                    observation.ActualOutcome == RuntimeBundleTestActualOutcomes.ExecutionError &&
                    string.IsNullOrEmpty(observation.ActualCode));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static BundleAcceptanceTestDocument RelationUserTaskPermissionDocument(
        string suffix,
        string seedHash) => new()
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest
            {
                Key = $"relation_user_task_assignment_{suffix}",
                Label = "Relation user task assignment is exact"
            },
            Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
            Actors =
            [
                new BundleAcceptanceActor { Id = "requester_actor", Roles = ["operator"] },
                new BundleAcceptanceActor { Id = "subject_manager_actor", Roles = ["manager"] },
                new BundleAcceptanceActor { Id = "decoy_manager_actor", Roles = ["manager"] },
                new BundleAcceptanceActor { Id = "reviewer_actor", Roles = ["reviewer"] }
            ],
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = "create_subject_customer",
                    Type = "create_record",
                    Entity = "customer",
                    AsActor = "requester_actor",
                    AsRole = "operator",
                    Values = new Dictionary<string, object?> { ["name"] = $"Subject customer {suffix}" }
                },
                new BundleAcceptanceStep
                {
                    Id = "bind_subject_manager",
                    Type = BundleAcceptanceStepTypes.BindActorRecord,
                    Entity = "customer",
                    Record = new BundleRecordSelector { Ref = "create_subject_customer" },
                    AsActor = "subject_manager_actor",
                    AsRole = "manager"
                },
                new BundleAcceptanceStep
                {
                    Id = "create_decoy_customer",
                    Type = "create_record",
                    Entity = "customer",
                    AsActor = "requester_actor",
                    AsRole = "operator",
                    Values = new Dictionary<string, object?> { ["name"] = $"Decoy customer {suffix}" }
                },
                new BundleAcceptanceStep
                {
                    Id = "bind_decoy_manager",
                    Type = BundleAcceptanceStepTypes.BindActorRecord,
                    Entity = "customer",
                    Record = new BundleRecordSelector { Ref = "create_decoy_customer" },
                    AsActor = "decoy_manager_actor",
                    AsRole = "manager"
                },
                new BundleAcceptanceStep
                {
                    Id = "create_subject_job",
                    Type = "create_record",
                    Entity = "job",
                    AsActor = "requester_actor",
                    AsRole = "operator",
                    Values = new Dictionary<string, object?>
                    {
                        ["name"] = $"Subject relation job {suffix}",
                        ["status"] = "open",
                        ["customer"] = new Dictionary<string, object?> { ["ref"] = "create_subject_customer" }
                    }
                },
                new BundleAcceptanceStep
                {
                    Id = "create_decoy_job",
                    Type = "create_record",
                    Entity = "job",
                    AsActor = "requester_actor",
                    AsRole = "operator",
                    Values = new Dictionary<string, object?>
                    {
                        ["name"] = $"Decoy relation job {suffix}",
                        ["status"] = "open",
                        ["customer"] = new Dictionary<string, object?> { ["ref"] = "create_decoy_customer" }
                    }
                },
                new BundleAcceptanceStep
                {
                    Id = "start_subject_relation_approval",
                    Type = "start_workflow_action",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    Action = "submit_relation_approval",
                    AsActor = "requester_actor",
                    AsRole = "operator"
                },
                new BundleAcceptanceStep
                {
                    Id = "start_decoy_relation_approval",
                    Type = "start_workflow_action",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_decoy_job" },
                    Action = "submit_relation_approval",
                    AsActor = "requester_actor",
                    AsRole = "operator"
                },
                new BundleAcceptanceStep
                {
                    Id = "reviewer_relation_approval_denied",
                    Type = "approve_task",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    WorkflowRef = "start_subject_relation_approval",
                    AsActor = "reviewer_actor",
                    AsRole = "reviewer",
                    Expected = new BundleAcceptanceExpectedOutcome
                    {
                        Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                        Code = BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                        Target = "linked_manager_approval"
                    }
                },
                new BundleAcceptanceStep
                {
                    Id = "subject_manager_approval_succeeds",
                    Type = "approve_task",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    WorkflowRef = "start_subject_relation_approval",
                    AsActor = "subject_manager_actor",
                    AsRole = "manager"
                }
            ],
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Id = "subject_unchanged_after_relation_denial",
                    AfterStep = "reviewer_relation_approval_denied",
                    Type = "record_field_equals",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    Field = "status",
                    Value = "open"
                },
                new BundleAcceptanceAssertion
                {
                    Id = "subject_relation_task_pending",
                    AfterStep = "reviewer_relation_approval_denied",
                    Type = "task_count",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    WorkflowRef = "start_subject_relation_approval",
                    TaskStep = "linked_manager_approval",
                    Status = "pending",
                    Count = 1
                },
                new BundleAcceptanceAssertion
                {
                    Id = "subject_relation_approved",
                    AfterStep = "subject_manager_approval_succeeds",
                    Type = "record_field_equals",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    Field = "status",
                    Value = "approved"
                },
                new BundleAcceptanceAssertion
                {
                    Id = "decoy_relation_still_waiting",
                    AfterStep = "subject_manager_approval_succeeds",
                    Type = "workflow_status",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_decoy_job" },
                    Workflow = "job_relation_approval",
                    WorkflowRef = "start_decoy_relation_approval",
                    Status = "waiting"
                }
            ]
        };

    private static BundleAcceptanceTestDocument WorkflowTaskPermissionDocument(
        string suffix,
        string seedHash,
        string deniedActorId,
        string deniedRole,
        string deniedCode,
        string deniedTarget,
        bool includePositiveControl)
    {
        var steps = new List<BundleAcceptanceStep>
        {
            new()
            {
                Id = "create_subject_job",
                Type = "create_record",
                Entity = "job",
                AsActor = "requester_actor",
                AsRole = "operator",
                Values = new Dictionary<string, object?>
                {
                    ["name"] = $"Subject workflow permission job {suffix}",
                    ["status"] = "open"
                }
            },
            new()
            {
                Id = "create_decoy_job",
                Type = "create_record",
                Entity = "job",
                AsActor = "requester_actor",
                AsRole = "operator",
                Values = new Dictionary<string, object?>
                {
                    ["name"] = $"Decoy workflow permission job {suffix}",
                    ["status"] = "open"
                }
            },
            new()
            {
                Id = "start_subject_approval",
                Type = "start_workflow_action",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_subject_job" },
                Action = "submit_for_approval",
                AsActor = "requester_actor",
                AsRole = "operator"
            },
            new()
            {
                Id = "start_decoy_approval",
                Type = "start_workflow_action",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_decoy_job" },
                Action = "submit_for_approval",
                AsActor = "requester_actor",
                AsRole = "operator"
            },
            new()
            {
                Id = "reviewer_approval_denied",
                Type = "approve_task",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_subject_job" },
                WorkflowRef = "start_subject_approval",
                AsActor = deniedActorId,
                AsRole = deniedRole,
                Expected = new BundleAcceptanceExpectedOutcome
                {
                    Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    Code = deniedCode,
                    Target = deniedTarget
                }
            }
        };
        if (includePositiveControl)
        {
            steps.Add(new BundleAcceptanceStep
            {
                Id = "manager_approval_succeeds",
                Type = "approve_task",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_subject_job" },
                WorkflowRef = "start_subject_approval",
                AsActor = "manager_actor",
                AsRole = "manager"
            });
        }

        var assertions = new List<BundleAcceptanceAssertion>();
        if (includePositiveControl)
        {
            assertions.AddRange(
            [
                new BundleAcceptanceAssertion
                {
                    Id = "subject_unchanged_after_denial",
                    AfterStep = "reviewer_approval_denied",
                    Type = "record_field_equals",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    Field = "status",
                    Value = "open"
                },
                new BundleAcceptanceAssertion
                {
                    Id = "subject_waiting_after_denial",
                    AfterStep = "reviewer_approval_denied",
                    Type = "workflow_status",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    Workflow = "job_approval",
                    WorkflowRef = "start_subject_approval",
                    Status = "waiting"
                },
                new BundleAcceptanceAssertion
                {
                    Id = "subject_task_pending_after_denial",
                    AfterStep = "reviewer_approval_denied",
                    Type = "task_count",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    WorkflowRef = "start_subject_approval",
                    TaskStep = "manager_approval",
                    Status = "pending",
                    Count = 1
                },
                new BundleAcceptanceAssertion
                {
                    Id = "subject_approved_by_manager",
                    AfterStep = "manager_approval_succeeds",
                    Type = "record_field_equals",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    Field = "status",
                    Value = "approved"
                },
                new BundleAcceptanceAssertion
                {
                    Id = "subject_completed_by_manager",
                    AfterStep = "manager_approval_succeeds",
                    Type = "workflow_status",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_subject_job" },
                    Workflow = "job_approval",
                    WorkflowRef = "start_subject_approval",
                    Status = "completed"
                },
                new BundleAcceptanceAssertion
                {
                    Id = "decoy_still_waiting",
                    AfterStep = "manager_approval_succeeds",
                    Type = "workflow_status",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_decoy_job" },
                    Workflow = "job_approval",
                    WorkflowRef = "start_decoy_approval",
                    Status = "waiting"
                }
            ]);
        }

        return new BundleAcceptanceTestDocument
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest
            {
                Key = $"workflow_task_assignment_{suffix}",
                Label = "Workflow task assignment denial is exact"
            },
            Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = seedHash },
            Actors =
            [
                new BundleAcceptanceActor { Id = "requester_actor", Roles = ["operator"] },
                new BundleAcceptanceActor { Id = "reviewer_actor", Roles = ["reviewer"] },
                new BundleAcceptanceActor { Id = "manager_actor", Roles = ["manager"] }
            ],
            Steps = steps,
            Assertions = assertions
        };
    }

    private static ErpBundle CreateWorkflowTaskPermissionBundle(bool includeRelationWorkflow = false)
    {
        var bundle = CreateExpectedOutcomeBundle();
        bundle.Roles["operator"].Grants.Add(new ErpGrant { Action = "create", Entity = "customer" });
        bundle.Roles["operator"].Grants.Add(new ErpGrant { Action = "read", Entity = "customer" });
        bundle.Roles["reviewer"] = new ErpRole
        {
            DisplayName = "Reviewer",
            Grants =
            [
                new ErpGrant { Action = "read", Entity = "job" },
                new ErpGrant { Action = "read_task", Entity = "job" },
                new ErpGrant { Action = "approve_task", Entity = "job" },
                new ErpGrant { Action = "reject_task", Entity = "job" }
            ]
        };
        if (includeRelationWorkflow)
        {
            bundle.Modules.Single(module => module.Id == "operations").Workflows.Add(new ErpWorkflow
            {
                Id = "job_relation_approval",
                Name = "Job relation approval",
                Entity = "job",
                Trigger = new ErpWorkflowTrigger { Action = "submit_relation_approval" },
                Steps =
                [
                    new ErpWorkflowStep
                    {
                        Id = "linked_manager_approval",
                        Type = "approval",
                        Label = "Linked manager approval",
                        AssignedTo = new ErpWorkflowAssignment
                        {
                            Type = "relation_user",
                            Path = "customer",
                            RequiredRole = "manager"
                        },
                        ApproveLabel = "Approve",
                        RejectLabel = "Reject"
                    },
                    new ErpWorkflowStep
                    {
                        Id = "mark_relation_approved",
                        Type = "update_record",
                        Set = new Dictionary<string, string> { ["status"] = "approved" }
                    }
                ]
            });
        }

        return bundle;
    }

    private static ErpBundle CreateExpectedOutcomeBundle() => new()
    {
        Id = "typed_acceptance_outcomes",
        Name = "Typed Acceptance Outcomes",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "operations",
                Name = "Operations",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "customer",
                        Name = "customers",
                        DisplayName = "Customers",
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "job",
                        Name = "jobs",
                        DisplayName = "Jobs",
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                            new ErpField { Id = "status", Name = "status", DisplayName = "Status", Type = "string", Required = true },
                            new ErpField { Id = "customer", Name = "customer_id", DisplayName = "Customer", Type = "relation", References = "customer" },
                            new ErpField { Id = "owner_principal", Name = "owner_principal", DisplayName = "Owner principal", Type = "integer" },
                            new ErpField { Id = "assignee_principal", Name = "assignee_principal", DisplayName = "Assignee principal", Type = "decimal" }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "log",
                        Name = "logs",
                        DisplayName = "Logs",
                        Fields =
                        [
                            new ErpField { Id = "job", Name = "job_id", DisplayName = "Job", Type = "relation", Required = true, References = "job" },
                            new ErpField { Id = "message", Name = "message", DisplayName = "Message", Type = "string", Required = true }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "activity_entry",
                        Name = "activity_entries",
                        DisplayName = "Activity Entries",
                        Fields =
                        [
                            new ErpField { Id = "job", Name = "job_id", DisplayName = "Job", Type = "relation", Required = true, References = "job" },
                            new ErpField { Id = "message", Name = "message", DisplayName = "Message", Type = "string", Required = true }
                        ]
                    }
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = "job_approval",
                        Name = "Job Approval",
                        Entity = "job",
                        Trigger = new ErpWorkflowTrigger { Action = "submit_for_approval" },
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "manager_approval",
                                Type = "approval",
                                Label = "Manager Approval",
                                AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "manager" },
                                ApproveLabel = "Approve",
                                RejectLabel = "Reject"
                            },
                            new ErpWorkflowStep
                            {
                                Id = "mark_approved",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["status"] = "approved" }
                            }
                        ],
                        OnRejected =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "mark_rejected",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["status"] = "rejected" }
                            }
                        ]
                    }
                ]
            }
        ],
        Actions =
        [
            new ErpAction
            {
                Key = "guarded_create_log",
                Label = "Guarded create log",
                Entity = "job",
                Type = "record_action",
                VisibleWhen =
                [
                    new ErpActionCondition
                    {
                        Op = "neq",
                        Left = new ErpActionOperand { Field = "status" },
                        Right = new ErpActionOperand { Value = "closed" }
                    }
                ],
                Requires = new ErpActionRequires
                {
                    Permissions = [new ErpGrant { Action = "create", Entity = "log" }]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "create_log",
                        Type = "create_record",
                        Entity = "log",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["job"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                            ["message"] = new() { Value = "must roll back" }
                        }
                    },
                    new ErpActionStep
                    {
                        Id = "stop_after_create",
                        Type = "guard",
                        Condition = new ErpActionCondition
                        {
                            Op = "eq",
                            Left = new ErpActionOperand { Value = false },
                            Right = new ErpActionOperand { Value = true }
                        },
                        Error = "Guard rejected after the provisional log was created."
                    }
                ]
            },
            new ErpAction
            {
                Key = "create_activity_entry",
                Label = "Create activity entry",
                Entity = "job",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions = [new ErpGrant { Action = "create", Entity = "activity_entry" }]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "create_activity_entry_record",
                        Type = "create_record",
                        Entity = "activity_entry",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["job"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                            ["message"] = new() { Value = "created by action" }
                        }
                    },
                    new ErpActionStep
                    {
                        Id = "return_activity_entry",
                        Type = "return_result",
                        Return = new ErpActionReturn
                        {
                            Step = "create_activity_entry_record",
                            Entity = "activity_entry",
                            Message = "Activity entry created."
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "allowed_action",
                Label = "Allowed action",
                Entity = "job",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions = [new ErpGrant { Action = "update", Entity = "job" }]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "keep_status",
                        Type = "update_current_record",
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["status"] = new() { Value = "open" }
                        }
                    }
                ]
            }
        ],
        Reports =
        [
            new ErpReport
            {
                Key = "jobs_summary",
                Label = "Jobs summary",
                Source = "job",
                Metrics =
                [
                    new ErpReportMetric { Key = "total_jobs", Label = "Total jobs", Op = "count" }
                ]
            },
            new ErpReport
            {
                Key = "activity_by_job",
                Label = "Activity by job",
                Source = "activity_entry",
                GroupBy =
                [
                    new ErpReportGroupBy { Field = "job", Label = "Job" }
                ],
                Metrics =
                [
                    new ErpReportMetric
                    {
                        Key = "total_activities",
                        Label = "Total activities",
                        Op = "count"
                    }
                ]
            }
        ],
        Dashboard = new ErpDashboard
        {
            Widgets =
            [
                new ErpDashboardWidget
                {
                    Key = "jobs_total",
                    Label = "Total jobs",
                    Type = "metric",
                    Report = "jobs_summary",
                    Metric = "total_jobs"
                }
            ]
        },
        Roles = new Dictionary<string, ErpRole>
        {
            ["operator"] = new()
            {
                DisplayName = "Operator",
                Grants =
                [
                    new ErpGrant { Action = "create", Entity = "job" },
                    new ErpGrant { Action = "read", Entity = "job" },
                    new ErpGrant
                    {
                        Action = "read",
                        Entity = "job",
                        Scope = PermissionGrantScopes.Record,
                        RecordScope = new ErpRecordScope
                        {
                            Field = "owner_principal",
                            Source = RecordPermissionScopeSources.ActorPrincipal
                        }
                    },
                    new ErpGrant { Action = "create", Entity = "log" },
                    new ErpGrant { Action = "create", Entity = "activity_entry" },
                    new ErpGrant { Action = "read", Entity = "activity_entry" },
                    new ErpGrant { Action = "update", Entity = "job" },
                    new ErpGrant { Action = "start_workflow", Entity = "job" },
                    new ErpGrant
                    {
                        Action = GranularPermissionActions.Execute,
                        Entity = "job",
                        Scope = PermissionGrantScopes.Action,
                        Target = "guarded_create_log"
                    },
                    new ErpGrant
                    {
                        Action = GranularPermissionActions.Execute,
                        Entity = "job",
                        Scope = PermissionGrantScopes.Action,
                        Target = "create_activity_entry"
                    },
                    new ErpGrant
                    {
                        Action = GranularPermissionActions.Execute,
                        Entity = "job",
                        Scope = PermissionGrantScopes.Action,
                        Target = "allowed_action"
                    }
                ]
            },
            ["manager"] = new()
            {
                DisplayName = "Manager",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "job" },
                    new ErpGrant { Action = "update", Entity = "job" },
                    new ErpGrant { Action = "approve_task", Entity = "job" },
                    new ErpGrant { Action = "reject_task", Entity = "job" },
                    new ErpGrant { Action = "read_task", Entity = "job" }
                ]
            },
            ["linked_operator"] = new()
            {
                DisplayName = "Linked operator",
                Grants =
                [
                    new ErpGrant { Action = "create", Entity = "customer" },
                    new ErpGrant { Action = "read", Entity = "customer" },
                    new ErpGrant
                    {
                        Action = "create",
                        Entity = "job",
                        Scope = PermissionGrantScopes.Record,
                        RecordScope = new ErpRecordScope
                        {
                            Field = "customer",
                            Source = RecordPermissionScopeSources.LinkedRecord,
                            LinkedEntity = "customer"
                        }
                    },
                    new ErpGrant
                    {
                        Action = "read",
                        Entity = "job",
                        Scope = PermissionGrantScopes.Record,
                        RecordScope = new ErpRecordScope
                        {
                            Field = "customer",
                            Source = RecordPermissionScopeSources.LinkedRecord,
                            LinkedEntity = "customer"
                        }
                    }
                ]
            },
            ["restricted"] = new()
            {
                DisplayName = "Restricted",
                Grants =
                [
                    new ErpGrant { Action = "create", Entity = "job" },
                    new ErpGrant { Action = "create", Entity = "log" },
                    new ErpGrant { Action = "update", Entity = "job" },
                    new ErpGrant
                    {
                        Action = GranularPermissionActions.Execute,
                        Entity = "job",
                        Scope = PermissionGrantScopes.Action,
                        Target = "allowed_action"
                    }
                ]
            }
        }
    };

    private static List<BundleAcceptanceTestDocument> CreateExpectedOutcomeTests(string suffix) =>
    [
        new BundleAcceptanceTestDocument
        {
            SourcePath = "guard-rollback.test.yaml",
            Test = new BundleAcceptanceTest { Key = "guard_rollback", Label = "Guard rejection rolls back prior action effects" },
            Setup = new BundleAcceptanceSetup { Seed = "none" },
            Steps =
            [
                CreateJobStep("guard_job", $"Guard Job {suffix}", "open", "operator"),
                RunActionStep(
                    "guard_action",
                    "guard_job",
                    "operator",
                    BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                    BundleAcceptanceExpectedOutcomeCodes.ActionGuard,
                    "stop_after_create")
            ],
            Assertions = [new BundleAcceptanceAssertion { Type = "record_count", Entity = "log", Count = 0 }]
        },
        new BundleAcceptanceTestDocument
        {
            SourcePath = "not-available.test.yaml",
            Test = new BundleAcceptanceTest { Key = "not_available", Label = "Unavailable action is an exact conflict" },
            Setup = new BundleAcceptanceSetup { Seed = "none" },
            Steps =
            [
                CreateJobStep("closed_job", $"Closed Job {suffix}", "closed", "operator"),
                RunActionStep(
                    "closed_action",
                    "closed_job",
                    "operator",
                    BundleAcceptanceExpectedOutcomes.ActionConflict,
                    BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
                    "guarded_create_log")
            ],
            Assertions = [new BundleAcceptanceAssertion { Type = "record_count", Entity = "log", Count = 0 }]
        },
        new BundleAcceptanceTestDocument
        {
            SourcePath = "named-action-denied.test.yaml",
            Test = new BundleAcceptanceTest { Key = "named_action_denied", Label = "Named action denial is typed" },
            Setup = new BundleAcceptanceSetup { Seed = "none" },
            Steps =
            [
                CreateJobStep("restricted_job", $"Restricted Job {suffix}", "open", "restricted"),
                RunActionStep(
                    "restricted_action",
                    "restricted_job",
                    "restricted",
                    BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    BundleAcceptanceExpectedOutcomeCodes.PermissionNamedActionExecute,
                    "guarded_create_log")
            ],
            Assertions = [new BundleAcceptanceAssertion { Type = "record_count", Entity = "log", Count = 0 }]
        },
        new BundleAcceptanceTestDocument
        {
            SourcePath = "workflow-rejected.test.yaml",
            Test = new BundleAcceptanceTest { Key = "workflow_rejected", Label = "Workflow rejection is a typed result" },
            Setup = new BundleAcceptanceSetup { Seed = "none" },
            Steps =
            [
                CreateJobStep("workflow_job", $"Workflow Job {suffix}", "open", "operator"),
                new BundleAcceptanceStep
                {
                    Id = "start_job_workflow",
                    Type = "start_workflow_action",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "workflow_job" },
                    Action = "submit_for_approval",
                    AsRole = "operator"
                },
                new BundleAcceptanceStep
                {
                    Id = "reject_job_workflow",
                    Type = "reject_task",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "workflow_job" },
                    WorkflowRef = "start_job_workflow",
                    AsRole = "manager",
                    Expected = new BundleAcceptanceExpectedOutcome
                    {
                        Outcome = BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                        Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected,
                        Target = "manager_approval"
                    }
                }
            ],
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Type = "record_field_equals",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "workflow_job" },
                    Field = "status",
                    Value = "rejected"
                }
            ]
        },
        new BundleAcceptanceTestDocument
        {
            SourcePath = "duplicate-workflow-start.test.yaml",
            Test = new BundleAcceptanceTest
            {
                Key = "duplicate_workflow_start",
                Label = "Duplicate workflow start is an exact action conflict"
            },
            Setup = new BundleAcceptanceSetup { Seed = "none" },
            Steps =
            [
                CreateJobStep("duplicate_workflow_job", $"Duplicate Workflow Job {suffix}", "open", "operator"),
                new BundleAcceptanceStep
                {
                    Id = "start_job_approval",
                    Type = "start_workflow_action",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "duplicate_workflow_job" },
                    Action = "submit_for_approval",
                    AsRole = "operator"
                },
                new BundleAcceptanceStep
                {
                    Id = "duplicate_job_approval",
                    Type = "start_workflow_action",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "duplicate_workflow_job" },
                    Action = "submit_for_approval",
                    AsRole = "operator",
                    Expected = new BundleAcceptanceExpectedOutcome
                    {
                        Outcome = BundleAcceptanceExpectedOutcomes.ActionConflict,
                        Code = BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
                        Target = "submit_for_approval"
                    }
                }
            ],
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Type = "workflow_status",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "duplicate_workflow_job" },
                    Workflow = "job_approval",
                    Status = "waiting"
                }
            ]
        }
    ];

    private static BundleAcceptanceStep CreateJobStep(
        string id,
        string name,
        string status,
        string role) =>
        new()
        {
            Id = id,
            Type = "create_record",
            Entity = "job",
            AsRole = role,
            Values = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["status"] = status
            }
        };

    private static BundleAcceptanceStep RunActionStep(
        string id,
        string recordRef,
        string role,
        string outcome,
        string code,
        string target) =>
        new()
        {
            Id = id,
            Type = "run_action",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = recordRef },
            Action = "guarded_create_log",
            AsRole = role,
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = outcome,
                Code = code,
                Target = target
            }
        };
}
