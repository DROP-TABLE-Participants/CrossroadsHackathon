using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeAcceptanceCheckpointHardeningTests
{
    [Fact]
    public void Acceptance_evidence_compares_database_datetimes_as_utc_instants()
    {
        RuntimeBundleTestRunner.ValuesEqual(
                new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc),
                "2026-01-02T10:00:00+02:00")
            .Should().BeTrue();
        RuntimeBundleTestRunner.ValuesEqual(
                new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero),
                "2026-01-02T08:00:01Z")
            .Should().BeFalse();
        RuntimeBundleTestRunner.ValuesEqual(
                new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                "2026-01-02")
            .Should().BeFalse("a date-only value must not masquerade as an exact datetime instant");
    }

    [Fact]
    public void Passing_acceptance_evidence_api_requires_exact_suite_hash()
    {
        var overloads = typeof(RuntimeBundleTestRunner)
            .GetMethods()
            .Where(method => method.Name == nameof(RuntimeBundleTestRunner.HasPassingResultAsync))
            .ToArray();

        overloads.Should().ContainSingle();
        overloads[0].GetParameters().Select(parameter => parameter.Name).Should().Equal(
            "tenant",
            "bundleVersion",
            "bundleHash",
            "testSuiteHash",
            "cancellationToken");
        overloads[0].GetParameters()[3].ParameterType.Should().Be<string>();
    }

    [Fact]
    public void Runtime_broker_rejects_controller_smoke_as_business_coverage()
    {
        var smoke = new BundleAcceptanceTestDocument
        {
            Test = new BundleAcceptanceTest
            {
                Key = "controller_smoke",
                Label = "Controller smoke",
                Origin = BundleAcceptanceTestOrigins.ControllerSmoke
            },
            Setup = new BundleAcceptanceSetup { Seed = "demo" },
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Type = "record_exists",
                    Entity = "customer"
                }
            ]
        };

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([smoke]);

        validate.Should().Throw<Exception>()
            .WithMessage("*controller smoke cannot substitute*");
    }

    [Fact]
    public void Runtime_broker_binds_all_v3_actor_roles_to_compiled_roles_without_requiring_grants()
    {
        var negativePermission = NegativePermissionDocument("restricted");
        BundleAcceptanceTestContractHasher.Stamp([negativePermission]);
        var declaredWithoutGrants = CompiledWithRoles(new RoleDefinition("restricted", "Restricted", []));
        var missing = CompiledWithRoles(new RoleDefinition("operator", "Operator", []));
        var reserved = NegativePermissionDocument("restricted");
        reserved.Actors[0].Roles.Add(RuntimeSystemRoleIds.TenantOwner);

        var validateDeclared = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [negativePermission],
            declaredWithoutGrants);
        var validateMissing = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [negativePermission],
            missing);
        var validateReserved = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [reserved],
            declaredWithoutGrants);

        validateDeclared.Should().NotThrow();
        validateMissing.Should().Throw<Exception>().WithMessage("*not emitted by the compiled bundle*");
        validateReserved.Should().Throw<Exception>().WithMessage("*reserved system role*");
    }

    [Fact]
    public void Runtime_broker_rejects_lower_snake_entity_aliases_for_actor_bindings()
    {
        var bundle = new ErpBundle
        {
            Id = "canonical_bind",
            Name = "Canonical bind",
            Version = "1.0.0",
            Modules =
            [
                new ErpModule
                {
                    Id = "crm",
                    Name = "CRM",
                    Entities =
                    [
                        new ErpEntity
                        {
                            Id = "customer",
                            Name = "customers",
                            DisplayName = "Customers",
                            Fields =
                            [
                                new ErpField
                                {
                                    Id = "name",
                                    Name = "name",
                                    DisplayName = "Name",
                                    Type = "string",
                                    Required = true
                                }
                            ]
                        }
                    ]
                }
            ],
            Roles = new Dictionary<string, ErpRole>
            {
                ["sales"] = new()
                {
                    DisplayName = "Sales",
                    Grants = [new ErpGrant { Action = "create", Entity = "customer" }]
                }
            }
        };
        var compiled = new ErpBundleCompiler().Compile(bundle);
        var test = new BundleAcceptanceTestDocument
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest { Key = "alias_bind", Label = "Alias bind is rejected" },
            Setup = new BundleAcceptanceSetup { Seed = "demo", SeedHash = new string('a', 64) },
            Actors = [new BundleAcceptanceActor { Id = "sales_actor", Roles = ["sales"] }],
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = "create_customer",
                    Type = BundleAcceptanceStepTypes.CreateRecord,
                    Entity = "customers",
                    AsActor = "sales_actor",
                    AsRole = "sales",
                    Values = new Dictionary<string, object?> { ["name"] = "Acme" }
                },
                new BundleAcceptanceStep
                {
                    Id = "bind_customer",
                    Type = BundleAcceptanceStepTypes.BindActorRecord,
                    Entity = "customers",
                    Record = new BundleRecordSelector { Ref = "create_customer" },
                    AsActor = "sales_actor",
                    AsRole = "sales"
                }
            ],
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Id = "customer_exists",
                    AfterStep = "bind_customer",
                    Type = "record_field_equals",
                    Entity = "customers",
                    Record = new BundleRecordSelector { Ref = "create_customer" },
                    Field = "name",
                    Value = "Acme"
                }
            ]
        };
        BundleAcceptanceTestContractHasher.Stamp([test]);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([test], compiled);

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*producer 'create_customer' without one exact explicitly returned created record*");
    }

    [Fact]
    public void Workflow_task_selection_uses_exact_started_instance_not_another_pending_task_on_the_record()
    {
        var intended = Task(id: 41, instanceId: 101, workflowId: "job_approval");
        var newerButWrong = Task(id: 42, instanceId: 202, workflowId: "job_rework");

        var selected = RuntimeBundleTestRunner.SelectExactPendingTask(
            [newerButWrong, intended],
            instanceId: 101,
            workflowId: "job_approval",
            entityId: "job",
            recordId: 7,
            workflowRef: "start_job_approval");

        selected.Id.Should().Be(41);
        selected.InstanceId.Should().Be(101);
    }

    [Fact]
    public void Expected_denial_task_selection_uses_the_exact_task_captured_by_workflow_ref()
    {
        var intended = Task(id: 41, instanceId: 101, workflowId: "job_approval");
        var started = new RuntimeWorkflowStartResult(
            Instance(instanceId: 101, workflowId: "job_approval", recordId: 7),
            intended);

        var selected = RuntimeBundleTestRunner.SelectExactReferencedPendingTask(
            started,
            entityId: "job",
            recordId: 7,
            workflowRef: "start_job_approval");

        selected.Should().BeSameAs(intended);
    }

    [Theory]
    [InlineData(202, "job_approval", "job", 7, "pending")]
    [InlineData(101, "job_rework", "job", 7, "pending")]
    [InlineData(101, "job_approval", "job", 8, "pending")]
    [InlineData(101, "job_approval", "other_entity", 7, "pending")]
    [InlineData(101, "job_approval", "job", 7, "approved")]
    public void Expected_denial_task_selection_rejects_wrong_instance_workflow_entity_record_or_status(
        long taskInstanceId,
        string taskWorkflow,
        string taskEntity,
        long taskRecord,
        string taskStatus)
    {
        var started = new RuntimeWorkflowStartResult(
            Instance(instanceId: 101, workflowId: "job_approval", recordId: 7),
            Task(
                id: 41,
                instanceId: taskInstanceId,
                workflowId: taskWorkflow,
                entityId: taskEntity,
                recordId: taskRecord,
                status: taskStatus));

        var select = () => RuntimeBundleTestRunner.SelectExactReferencedPendingTask(
            started,
            entityId: "job",
            recordId: 7,
            workflowRef: "start_job_approval");

        select.Should().Throw<RuntimeBundleTestException>()
            .WithMessage("*one exact waiting instance and pending task*");
    }

    [Fact]
    public void Expected_denial_task_selection_rejects_a_task_that_is_not_the_instances_current_step()
    {
        var started = new RuntimeWorkflowStartResult(
            Instance(
                instanceId: 101,
                workflowId: "job_approval",
                recordId: 7,
                currentStepId: "other_approval"),
            Task(id: 41, instanceId: 101, workflowId: "job_approval"));

        var select = () => RuntimeBundleTestRunner.SelectExactReferencedPendingTask(
            started,
            entityId: "job",
            recordId: 7,
            workflowRef: "start_job_approval");

        select.Should().Throw<RuntimeBundleTestException>()
            .WithMessage("*one exact waiting instance and pending task*");
    }

    [Fact]
    public void Acceptance_live_binding_requires_exact_pending_waiting_current_task_and_assignment()
    {
        var instance = Instance(instanceId: 101, workflowId: "job_approval", recordId: 7);
        var task = Task(id: 41, instanceId: 101, workflowId: "job_approval");
        var expected = new RuntimeWorkflowStartResult(instance, task);

        RuntimeWorkflowStore.MatchesExactPendingAcceptanceBinding(expected, instance, task)
            .Should().BeTrue();

        var mismatches = new (RuntimeWorkflowInstance Instance, RuntimeWorkflowTask Task)[]
        {
            (instance with { Status = "completed" }, task),
            (instance with { CurrentStepId = "other_approval" }, task),
            (instance with { RecordId = 8 }, task),
            (instance, task with { Status = "approved" }),
            (instance, task with { InstanceId = 202 }),
            (instance, task with { StepId = "other_approval" }),
            (instance, task with { AssignedRole = "finance" }),
            (instance, task with { AssignedRole = null, AssignedUserId = -99 })
        };
        mismatches.Should().OnlyContain(candidate =>
            !RuntimeWorkflowStore.MatchesExactPendingAcceptanceBinding(
                expected,
                candidate.Instance,
                candidate.Task));
    }

    [Fact]
    public void V3_task_decision_requires_a_preceding_workflow_ref_for_the_same_record()
    {
        var valid = V3WorkflowDocument("start_approval", "create_job");
        var wrongRecord = V3WorkflowDocument("start_approval", "create_other_job");
        var unknownRef = V3WorkflowDocument("missing_start", "create_job");

        var stampValid = () => BundleAcceptanceTestContractHasher.Stamp([valid]);
        var stampWrongRecord = () => BundleAcceptanceTestContractHasher.Stamp([wrongRecord]);
        var stampUnknownRef = () => BundleAcceptanceTestContractHasher.Stamp([unknownRef]);

        stampValid.Should().NotThrow();
        stampWrongRecord.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*same entity and record*");
        stampUnknownRef.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*same entity and record*");
    }

    [Fact]
    public void V3_non_task_steps_require_an_exact_empty_workflow_ref()
    {
        var invalid = V3WorkflowDocument(
            "start_approval",
            "create_job",
            nonTaskWorkflowRef: "start_approval");

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([invalid]);

        stamp.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*must set workflow_ref to the empty string*");
    }

    [Fact]
    public void Non_task_observations_ignore_a_legacy_stray_workflow_ref()
    {
        var started = new RuntimeWorkflowStartResult(
            Instance(instanceId: 101, workflowId: "job_approval", recordId: 7),
            Task(id: 41, instanceId: 101, workflowId: "job_approval"));
        IReadOnlyDictionary<string, RuntimeWorkflowStartResult> starts =
            new Dictionary<string, RuntimeWorkflowStartResult>(StringComparer.OrdinalIgnoreCase)
            {
                ["start_approval"] = started
            };
        var nonTask = new BundleAcceptanceStep
        {
            Id = "create_job",
            Type = "create_record",
            WorkflowRef = "start_approval"
        };
        var decision = new BundleAcceptanceStep
        {
            Id = "approve_job",
            Type = "approve_task",
            WorkflowRef = "start_approval"
        };

        RuntimeBundleTestRunner.SelectReferencedWorkflowStartForObservation(nonTask, starts)
            .Should().BeNull();
        RuntimeBundleTestRunner.SelectReferencedWorkflowStartForObservation(decision, starts)
            .Should().BeSameAs(started);
    }

    [Fact]
    public void Legacy_v2_task_denial_shape_remains_hashable_for_unsupported_execution()
    {
        var legacy = new BundleAcceptanceTestDocument
        {
            Schema = BundleAcceptanceTestSchemas.V2,
            Test = new BundleAcceptanceTest { Key = "legacy_task_denial", Label = "Legacy task denial" },
            Setup = new BundleAcceptanceSetup { Seed = "demo" },
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = "approve",
                    Type = "approve_task",
                    Entity = "job",
                    AsRole = "reviewer",
                    Expected = new BundleAcceptanceExpectedOutcome
                    {
                        Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                        Code = BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                        Target = "manager_approval"
                    }
                }
            ]
        };

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([legacy]);

        stamp.Should().NotThrow();
    }

    [Fact]
    public void Workflow_ref_is_part_of_the_canonical_acceptance_contract_hash()
    {
        var document = WorkflowDocument("start_approval");
        BundleAcceptanceTestContractHasher.Stamp([document]);

        var changed = WorkflowDocument("start_other_workflow");
        changed.Test.ContractHash = document.Test.ContractHash;
        changed.Test.SuiteHash = document.Test.SuiteHash;
        changed.Test.SuiteIndex = document.Test.SuiteIndex;

        var verify = () => BundleAcceptanceTestContractHasher.Verify([changed]);

        verify.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*contract_hash does not match*");
    }

    private static BundleAcceptanceTestDocument WorkflowDocument(string workflowRef) => new()
    {
        Test = new BundleAcceptanceTest { Key = "workflow", Label = "Workflow" },
        Setup = new BundleAcceptanceSetup { Seed = "demo" },
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "start_approval",
                Type = "start_workflow_action",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "job" },
                Action = "submit",
                AsRole = "operator"
            },
            new BundleAcceptanceStep
            {
                Id = "approve",
                Type = "approve_task",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "job" },
                WorkflowRef = workflowRef,
                AsRole = "manager"
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Type = "workflow_status",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "job" },
                Workflow = "job_approval",
                Status = "completed"
            }
        ]
    };

    private static BundleAcceptanceTestDocument V3WorkflowDocument(
        string workflowRef,
        string decisionRecordRef,
        string nonTaskWorkflowRef = "") => new()
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest { Key = "v3_workflow", Label = "V3 workflow" },
            Setup = new BundleAcceptanceSetup { Seed = "demo", SeedHash = new string('a', 64) },
            Actors =
            [
                new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] },
                new BundleAcceptanceActor { Id = "manager_actor", Roles = ["manager"] }
            ],
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = "create_job",
                    Type = "create_record",
                    Entity = "job",
                    WorkflowRef = nonTaskWorkflowRef,
                    AsActor = "operator_actor",
                    AsRole = "operator",
                    Values = new Dictionary<string, object?> { ["name"] = "Subject" }
                },
                new BundleAcceptanceStep
                {
                    Id = "create_other_job",
                    Type = "create_record",
                    Entity = "job",
                    AsActor = "operator_actor",
                    AsRole = "operator",
                    Values = new Dictionary<string, object?> { ["name"] = "Other" }
                },
                new BundleAcceptanceStep
                {
                    Id = "start_approval",
                    Type = "start_workflow_action",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_job" },
                    Action = "submit",
                    AsActor = "operator_actor",
                    AsRole = "operator"
                },
                new BundleAcceptanceStep
                {
                    Id = "approve",
                    Type = "approve_task",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = decisionRecordRef },
                    WorkflowRef = workflowRef,
                    AsActor = "manager_actor",
                    AsRole = "manager"
                }
            ]
        };

    private static BundleAcceptanceTestDocument NegativePermissionDocument(string role) => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest
        {
            Key = "negative_permission",
            Label = "A role without grants is denied create",
            Origin = BundleAcceptanceTestOrigins.Authored
        },
        Setup = new BundleAcceptanceSetup
        {
            Seed = "demo",
            SeedHash = new string('b', 64)
        },
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
                Entity = "customer",
                AsActor = "restricted_actor",
                AsRole = role,
                Values = new Dictionary<string, object?> { ["name"] = "Denied" },
                Expected = new BundleAcceptanceExpectedOutcome
                {
                    Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    Code = BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate,
                    Target = "customer"
                }
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "no_customer_created",
                AfterStep = "attempt_create",
                Type = "record_count",
                Entity = "customer",
                Count = 0
            }
        ]
    };

    private static CompiledBundle CompiledWithRoles(params RoleDefinition[] roles) => new(
        "acceptance_roles",
        "Acceptance roles",
        "1.0.0",
        new string('a', 64),
        [],
        [],
        [],
        [],
        roles,
        [],
        new DashboardDefinition([]),
        [],
        []);

    private static RuntimeWorkflowTask Task(
        long id,
        long instanceId,
        string workflowId,
        string entityId = "job",
        long recordId = 7,
        string status = "pending") =>
        new(
            id,
            instanceId,
            workflowId,
            "manager_approval",
            "Manager approval",
            entityId,
            recordId,
            status,
            "manager",
            null,
            null,
            null,
            null,
            JsonDocument.Parse("{}").RootElement.Clone(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static RuntimeWorkflowInstance Instance(
        long instanceId,
        string workflowId,
        long recordId,
        string currentStepId = "manager_approval") =>
        new(
            instanceId,
            workflowId,
            "Job approval",
            "job",
            "job",
            recordId,
            "waiting",
            currentStepId,
            -1,
            null,
            JsonDocument.Parse("{}").RootElement.Clone(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
}
