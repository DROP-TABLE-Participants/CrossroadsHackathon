using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Scheduling;

namespace ErpGenerator.Runtime.Tests;

public sealed class RunDueScheduleAcceptanceContractTests
{
    [PostgresFact]
    public async Task V3_runner_proves_pre_due_noop_due_execution_and_repeat_one_shot_identity()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var database = $"erp_accept_schedule_{Guid.NewGuid():N}"[..47];
        await TestPostgres.CreateDatabaseAsync(admin, database);
        try
        {
            var tenant = RuntimeTestTenant.Create(
                "accept-schedule",
                "accept-schedule.erpgenerator.example",
                database);
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var compiled = new ErpBundleCompiler().Compile(Bundle());
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder())
                .ApplyCompiledBundleAsync(tenant, compiled);

            var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
            var clock = new FixedTimeProvider(now);
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var workflows = new RuntimeWorkflowStore(factory, permissions, clock);
            var options = new RuntimeScheduleOptions();
            var scheduleStore = new RuntimeScheduleStore(factory, options, clock);
            var scheduleCycle = new RuntimeScheduleCycle(
                scheduleStore,
                new RuntimeScheduledWorkflowExecutor(workflows),
                options);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                workflows,
                new RuntimeActionStore(factory, permissions),
                new RuntimeReportStore(factory),
                clock,
                scheduleCycle);
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "schedule_seed", Label = "Schedule seed" },
                Records = []
            };
            var document = AcceptanceDocument(seed, now);
            BundleAcceptanceTestContractHasher.Stamp([document]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [document]);

            result.Status.Should().Be("passed");
            var test = result.Results.Should().ContainSingle().Which;
            var before = test.StepObservations.Single(item => item.StepId == "before_due");
            var due = test.StepObservations.Single(item => item.StepId == "run_due");
            var repeat = test.StepObservations.Single(item => item.StepId == "repeat_due");
            var denied = test.StepObservations.Single(item => item.StepId == "deny_scheduled_approval");
            var approved = test.StepObservations.Single(item => item.StepId == "approve_scheduled_task");
            before.Should().Match<RuntimeBundleTestStepObservation>(item =>
                item.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                item.Schedule == "incident_due" &&
                item.ScheduleTime == BundleAcceptanceScheduleTimes.BeforeDue &&
                item.ScheduleJobStatus == RuntimeScheduleJobStatuses.Ready &&
                item.ScheduleJobId != null &&
                item.WorkflowInstanceId == null &&
                item.ScheduleCycleCompleted == 0);
            due.Should().Match<RuntimeBundleTestStepObservation>(item =>
                item.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                item.ScheduleJobId == before.ScheduleJobId &&
                item.ScheduleJobStatus == RuntimeScheduleJobStatuses.Completed &&
                item.WorkflowInstanceId != null &&
                item.TaskId != null &&
                item.WorkflowStep == "manager_approval" &&
                item.TaskAssignedRole == "operator" &&
                item.ScheduleCycleCompleted == 1);
            repeat.Should().Match<RuntimeBundleTestStepObservation>(item =>
                item.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                item.ScheduleJobId == due.ScheduleJobId &&
                item.WorkflowInstanceId == due.WorkflowInstanceId &&
                item.ScheduleJobStatus == RuntimeScheduleJobStatuses.Completed &&
                item.ScheduleCycleClaimed == 0 &&
                item.ScheduleCycleCompleted == 0);
            denied.Should().Match<RuntimeBundleTestStepObservation>(item =>
                item.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                item.WorkflowRef == "run_due" &&
                item.WorkflowInstanceId == due.WorkflowInstanceId &&
                item.TaskId == due.TaskId &&
                item.WorkflowStep == "manager_approval" &&
                item.ActualOutcome == BundleAcceptanceExpectedOutcomes.PermissionDenied &&
                item.ActualCode == BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment &&
                item.ActualTarget == "manager_approval");
            approved.Should().Match<RuntimeBundleTestStepObservation>(item =>
                item.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                item.WorkflowRef == "run_due" &&
                item.WorkflowInstanceId == due.WorkflowInstanceId &&
                item.TaskId == due.TaskId &&
                item.WorkflowStep == "manager_approval");
            test.AssertionObservations.Should().HaveCount(8)
                .And.OnlyContain(item => item.Status == RuntimeBundleTestAssertionObservationStatuses.Passed);
            test.AssertionObservations.Single(item => item.AssertionType == "notification_exists")
                .Notifications.Should().ContainSingle().Which.RecipientRole.Should().Be("operator");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, database);
        }
    }

    [Fact]
    public void V3_hash_binds_exact_schedule_and_closed_time_without_changing_empty_step_defaults()
    {
        var before = Contract("incident_due", BundleAcceptanceScheduleTimes.BeforeDue);
        var due = Contract("incident_due", BundleAcceptanceScheduleTimes.Due);
        var other = Contract("incident_reminder", BundleAcceptanceScheduleTimes.BeforeDue);

        var beforeHash = BundleAcceptanceTestContractHasher.Stamp([before]).Tests.Single()
            .StepBindings.Single(binding => binding.Id == "run_schedule").ContractHash;
        var dueHash = BundleAcceptanceTestContractHasher.Stamp([due]).Tests.Single()
            .StepBindings.Single(binding => binding.Id == "run_schedule").ContractHash;
        var otherHash = BundleAcceptanceTestContractHasher.Stamp([other]).Tests.Single()
            .StepBindings.Single(binding => binding.Id == "run_schedule").ContractHash;

        beforeHash.Should().NotBe(dueHash);
        beforeHash.Should().NotBe(otherHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("at_time")]
    [InlineData("2026-07-13T12:00:00Z")]
    [InlineData("* * * * *")]
    public void V3_rejects_every_open_or_unknown_schedule_time(string scheduleTime)
    {
        var document = Contract("incident_due", scheduleTime);

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([document]);

        stamp.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*closed schedule_time*");
    }

    [Fact]
    public void V2_rejects_the_V3_only_scheduler_operation()
    {
        var document = Contract("incident_due", BundleAcceptanceScheduleTimes.Due);
        document = new BundleAcceptanceTestDocument
        {
            Schema = BundleAcceptanceTestSchemas.V2,
            Test = document.Test,
            Setup = new BundleAcceptanceSetup { Seed = "empty" },
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = "run_schedule",
                    Type = BundleAcceptanceStepTypes.RunDueSchedule,
                    Entity = "incident",
                    Record = new BundleRecordSelector { Ref = "incident_ref" },
                    Schedule = "incident_due",
                    ScheduleTime = BundleAcceptanceScheduleTimes.Due,
                    AsRole = "owner"
                }
            ],
            Assertions = document.Assertions
        };

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([document]);

        stamp.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*v3-only field 'steps[].type=run_due_schedule'*");
    }

    [Fact]
    public void Compiler_backed_verification_rejects_wrong_schedule_and_wrong_entity()
    {
        var compiled = new ErpBundleCompiler().Compile(Bundle());
        var wrongSchedule = Contract("missing_schedule", BundleAcceptanceScheduleTimes.Due);
        BundleAcceptanceTestContractHasher.Stamp([wrongSchedule]);
        var verifyMissing = () => BundleAcceptanceTestContractHasher.Verify([wrongSchedule], compiled);
        verifyMissing.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*one exact compiled schedule*");

        var wrongEntity = Contract("incident_due", BundleAcceptanceScheduleTimes.Due, "customer");
        BundleAcceptanceTestContractHasher.Stamp([wrongEntity]);
        var verifyEntity = () => BundleAcceptanceTestContractHasher.Verify([wrongEntity], compiled);
        verifyEntity.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*one exact compiled schedule*");
    }

    [Fact]
    public void Runtime_reference_validation_rejects_unknown_record_ref_before_execution()
    {
        var document = Contract("incident_due", BundleAcceptanceScheduleTimes.Due);

        var validate = () => RuntimeBundleTestRunner.ValidateV3RecordReferences(document, []);

        validate.Should().Throw<RuntimeBundleTestException>()
            .WithMessage("*record ref 'incident_ref' outside its own seed and preceding successful producers*");
    }

    [Fact]
    public void Before_due_workflow_count_is_closed_to_exact_zero()
    {
        var seed = new BundleSeedDocument
        {
            Seed = new BundleSeed { Key = "schedule_seed", Label = "Schedule seed" }
        };
        var document = AcceptanceDocument(
            seed,
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        var assertion = document.Assertions.Single(item => item.Id == "no_workflow_before_due");
        document.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = assertion.Id,
            Type = assertion.Type,
            AfterStep = assertion.AfterStep,
            Entity = assertion.Entity,
            Record = assertion.Record,
            Workflow = assertion.Workflow,
            WorkflowRef = assertion.WorkflowRef,
            Count = 1
        };

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([document]);

        stamp.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*malformed workflow_instance_count contract*");
    }

    [Fact]
    public void Task_decisions_may_bind_an_exact_due_schedule_but_not_a_before_due_probe()
    {
        var due = Contract("incident_due", BundleAcceptanceScheduleTimes.Due);
        due.Steps.Add(TaskDecision("approve_due", "run_schedule", "incident_ref"));

        BundleAcceptanceTestContractHasher.Stamp([due]);

        var beforeDue = Contract("incident_due", BundleAcceptanceScheduleTimes.BeforeDue);
        beforeDue.Steps.Add(TaskDecision("approve_before_due", "run_schedule", "incident_ref"));
        var stampBeforeDue = () => BundleAcceptanceTestContractHasher.Stamp([beforeDue]);

        stampBeforeDue.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*one exact preceding successful workflow_ref start or due schedule*");
    }

    private static BundleAcceptanceTestDocument Contract(
        string schedule,
        string scheduleTime,
        string entity = "incident") => new()
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest
            {
                Key = "schedule_contract",
                Label = "Schedule contract"
            },
            Setup = new BundleAcceptanceSetup
            {
                Seed = "empty",
                SeedHash = new string('a', 64)
            },
            Actors =
        [
            new BundleAcceptanceActor { Id = "owner_actor", Roles = ["owner"] },
            new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] },
            new BundleAcceptanceActor { Id = "outsider_actor", Roles = ["outsider"] }
        ],
            Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "run_schedule",
                Type = BundleAcceptanceStepTypes.RunDueSchedule,
                Entity = entity,
                Record = new BundleRecordSelector { Ref = "incident_ref" },
                Schedule = schedule,
                ScheduleTime = scheduleTime,
                AsActor = "owner_actor",
                AsRole = "owner"
            }
        ]
        };

    private static BundleAcceptanceTestDocument AcceptanceDocument(
        BundleSeedDocument seed,
        DateTimeOffset now) => new()
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest
            {
                Key = "schedule_execution",
                Label = "Schedule executes exactly once"
            },
            Setup = new BundleAcceptanceSetup
            {
                Seed = seed.Seed.Key,
                SeedHash = BundleSeedContractHasher.ComputeHash(seed)
            },
            Actors =
        [
            new BundleAcceptanceActor { Id = "owner_actor", Roles = ["owner"] },
            new BundleAcceptanceActor { Id = "operator_actor", Roles = ["operator"] },
            new BundleAcceptanceActor { Id = "outsider_actor", Roles = ["outsider"] }
        ],
            Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "create_incident",
                Type = BundleAcceptanceStepTypes.CreateRecord,
                Entity = "incident",
                Values = new Dictionary<string, object?>
                {
                    ["title"] = "SLA breach",
                    ["due_at"] = now.AddHours(1),
                    ["status"] = "open"
                },
                AsActor = "owner_actor",
                AsRole = "owner"
            },
            ScheduleStep("before_due", BundleAcceptanceScheduleTimes.BeforeDue),
            new BundleAcceptanceStep
            {
                Id = "make_due",
                Type = "update_record",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                Set = new Dictionary<string, object?> { ["due_at"] = now.AddMinutes(-1) },
                AsActor = "owner_actor",
                AsRole = "owner"
            },
            ScheduleStep("run_due", BundleAcceptanceScheduleTimes.Due),
            ScheduleStep("repeat_due", BundleAcceptanceScheduleTimes.Due),
            new BundleAcceptanceStep
            {
                Id = "deny_scheduled_approval",
                Type = "approve_task",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                WorkflowRef = "run_due",
                AsActor = "outsider_actor",
                AsRole = "outsider",
                Expected = new BundleAcceptanceExpectedOutcome
                {
                    Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    Code = BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                    Target = "manager_approval"
                }
            },
            TaskDecision("approve_scheduled_task", "run_due")
        ],
            Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "no_workflow_before_due",
                Type = "workflow_instance_count",
                AfterStep = "before_due",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                Workflow = "escalate_incident",
                WorkflowRef = "before_due",
                Count = 0
            },
            new BundleAcceptanceAssertion
            {
                Id = "incident_escalated",
                Type = "record_field_equals",
                AfterStep = "run_due",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                Field = "status",
                Value = "escalated"
            },
            new BundleAcceptanceAssertion
            {
                Id = "scheduled_workflow_waiting",
                Type = "workflow_status",
                AfterStep = "run_due",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                Workflow = "escalate_incident",
                WorkflowRef = "run_due",
                Status = "waiting"
            },
            new BundleAcceptanceAssertion
            {
                Id = "scheduled_task_exists",
                Type = "task_exists",
                AfterStep = "run_due",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                WorkflowRef = "run_due",
                TaskStep = "manager_approval",
                Status = "pending"
            },
            new BundleAcceptanceAssertion
            {
                Id = "scheduled_notification_exists",
                Type = "notification_exists",
                AfterStep = "run_due",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                WorkflowRef = "run_due",
                TaskStep = "manager_approval"
            },
            new BundleAcceptanceAssertion
            {
                Id = "one_scheduled_workflow",
                Type = "workflow_instance_count",
                AfterStep = "run_due",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                Workflow = "escalate_incident",
                WorkflowRef = "run_due",
                Count = 1
            },
            new BundleAcceptanceAssertion
            {
                Id = "scheduled_task_survives_denial",
                Type = "task_exists",
                AfterStep = "deny_scheduled_approval",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                WorkflowRef = "run_due",
                TaskStep = "manager_approval",
                Status = "pending"
            },
            new BundleAcceptanceAssertion
            {
                Id = "scheduled_workflow_completed",
                Type = "workflow_status",
                AfterStep = "approve_scheduled_task",
                Entity = "incident",
                Record = new BundleRecordSelector { Ref = "create_incident" },
                Workflow = "escalate_incident",
                WorkflowRef = "run_due",
                Status = "completed"
            }
        ]
        };

    private static BundleAcceptanceStep TaskDecision(
        string id,
        string workflowRef,
        string recordRef = "create_incident") => new()
        {
            Id = id,
            Type = "approve_task",
            Entity = "incident",
            Record = new BundleRecordSelector { Ref = recordRef },
            WorkflowRef = workflowRef,
            AsActor = "operator_actor",
            AsRole = "operator"
        };

    private static BundleAcceptanceStep ScheduleStep(string id, string scheduleTime) => new()
    {
        Id = id,
        Type = BundleAcceptanceStepTypes.RunDueSchedule,
        Entity = "incident",
        Record = new BundleRecordSelector { Ref = "create_incident" },
        Schedule = "incident_due",
        ScheduleTime = scheduleTime,
        AsActor = "owner_actor",
        AsRole = "owner"
    };

    private static ErpBundle Bundle() => new()
    {
        Id = "schedule_contract_bundle",
        Name = "Schedule contract bundle",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "support",
                Name = "Support",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "incident",
                        Name = "incidents",
                        DisplayName = "Incidents",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "title", Name = "title", DisplayName = "Title",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "due_at", Name = "due_at", DisplayName = "Due at",
                                Type = "datetime", Required = true
                            },
                            new ErpField
                            {
                                Id = "status", Name = "status", DisplayName = "Status",
                                Type = "enum", Required = true, Options = ["open", "escalated"]
                            }
                        ]
                    }
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = "escalate_incident",
                        Name = "Escalate incident",
                        Entity = "incident",
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "escalate", Type = "update_record",
                                Set = new Dictionary<string, string> { ["status"] = "escalated" }
                            },
                            new ErpWorkflowStep
                            {
                                Id = "manager_approval",
                                Type = "approval",
                                Label = "Manager approval",
                                AssignedTo = new ErpWorkflowAssignment
                                {
                                    Type = "role",
                                    Role = "operator"
                                },
                                ApproveLabel = "Approve",
                                RejectLabel = "Reject"
                            }
                        ]
                    }
                ]
            }
        ],
        Schedules =
        [
            new ErpSchedule
            {
                Id = "incident_due",
                Entity = "incident",
                DueField = "due_at",
                Workflow = "escalate_incident",
                Filters =
                [
                    new ErpScheduleFilter
                    {
                        Field = "status", Op = "eq",
                        Value = new ErpTypedLiteral { Type = "string", Value = "open" }
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["owner"] = new()
            {
                DisplayName = "Owner",
                Grants =
                [
                    new ErpGrant { Action = "*", Entity = "*" },
                    new ErpGrant { Action = "read", Entity = "incident" },
                    new ErpGrant { Action = "read_task", Entity = "incident" },
                    new ErpGrant { Action = "approve_task", Entity = "incident" }
                ]
            },
            ["operator"] = new()
            {
                DisplayName = "Operator",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "incident" },
                    new ErpGrant { Action = "read_task", Entity = "incident" },
                    new ErpGrant { Action = "approve_task", Entity = "incident" }
                ]
            },
            ["outsider"] = new()
            {
                DisplayName = "Outsider",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "incident" },
                    new ErpGrant { Action = "read_task", Entity = "incident" },
                    new ErpGrant { Action = "approve_task", Entity = "incident" }
                ]
            }
        }
    };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
