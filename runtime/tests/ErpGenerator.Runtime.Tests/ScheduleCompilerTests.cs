using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class ScheduleCompilerTests
{
    [Fact]
    public void Compiler_emits_closed_one_shot_schedule_metadata_with_canonical_hash()
    {
        var first = ScheduledBundle(
        [
            Exists("attempts"),
            Equal("status", "string", "open")
        ]);
        var reordered = ScheduledBundle(
        [
            Equal("status", "string", "open"),
            Exists("attempts")
        ]);

        var compiled = new ErpBundleCompiler().Compile(first);
        var equivalent = new ErpBundleCompiler().Compile(reordered);

        var schedule = compiled.Schedules.Should().ContainSingle().Subject;
        schedule.Id.Should().Be("escalate_due_incident");
        schedule.Entity.Should().Be("incident");
        schedule.DueField.Should().Be("due_at");
        schedule.Workflow.Should().Be("escalate_incident");
        schedule.Filters.Select(filter => $"{filter.Field}:{filter.Op}").Should().Equal(
            "attempts:exists",
            "status:eq");
        schedule.Filters.Single(filter => filter.Field == "status").Value.Should().Be(
            new TypedLiteralDefinition("string", "open"));
        schedule.DefinitionHash.Should().MatchRegex("^[0-9a-f]{64}$");
        schedule.DefinitionHash.Should().Be(equivalent.Schedules.Single().DefinitionHash,
            "conjunctive filter order is not schedule semantics");
        compiled.BundleHash.Should().Be(equivalent.BundleHash,
            "bundle identity uses the canonical compiled schedule contract");
    }

    [Fact]
    public void Schedule_semantic_changes_are_bound_into_definition_and_bundle_hashes()
    {
        var open = new ErpBundleCompiler().Compile(ScheduledBundle([Equal("status", "string", "open")]));
        var closed = new ErpBundleCompiler().Compile(ScheduledBundle([Equal("status", "string", "closed")]));

        open.Schedules.Single().DefinitionHash.Should().NotBe(closed.Schedules.Single().DefinitionHash);
        open.BundleHash.Should().NotBe(closed.BundleHash);
    }

    [Fact]
    public void Decimal_schedule_literals_use_runtime_parseable_canonical_values()
    {
        var compiler = new ErpBundleCompiler();
        var exponent = compiler.Compile(ScheduledBundle([Equal("amount", "decimal", "1e1")]));
        var scaled = compiler.Compile(ScheduledBundle([Equal("amount", "decimal", 10.00m)]));

        var literal = exponent.Schedules.Single().Filters.Single().Value;
        literal.Should().Be(new TypedLiteralDefinition("decimal", "10"));
        exponent.Schedules.Single().DefinitionHash.Should().Be(scaled.Schedules.Single().DefinitionHash);
        exponent.BundleHash.Should().Be(scaled.BundleHash);
    }

    [Fact]
    public void Empty_schedule_collection_preserves_legacy_bundle_hash_identity()
    {
        var withDefaultEmptyCollection = BaseBundle();
        var hash = new ErpBundleCompiler().Compile(withDefaultEmptyCollection).BundleHash;

        withDefaultEmptyCollection.Schedules.Should().BeEmpty();
        hash.Should().Be("6c67245827b41789e9d049a6731149c1a700459e5e84fd8364cb9b916a72ece0");
    }

    [Fact]
    public void Yaml_loader_round_trips_exact_schedule_shape_and_exists_filters()
    {
        var bundle = new ErpBundleYamlLoader().Load(BundleYaml());
        var compiled = new ErpBundleCompiler().Compile(bundle);

        bundle.Schedules.Should().ContainSingle(schedule =>
            schedule.Id == "escalate_due_incident" &&
            schedule.DueField == "due_at" &&
            schedule.Workflow == "escalate_incident");
        bundle.Schedules.Single().Filters.Should().Contain(filter =>
            filter.Field == "attempts" && filter.Op == "not_exists" && filter.Value == null);
        compiled.Schedules.Single().Filters.Should().Contain(filter =>
            filter.Field == "status" &&
            filter.Value == new TypedLiteralDefinition("string", "open"));
    }

    [Fact]
    public void Yaml_loader_rejects_open_ended_schedule_members()
    {
        var yaml = BundleYaml().Replace(
            "    workflow: escalate_incident",
            "    workflow: escalate_incident\n    cron: '* * * * *'\n    timezone: Europe/Sofia\n    action: unsafe_target",
            StringComparison.Ordinal);

        var load = () => new ErpBundleYamlLoader().Load(yaml);

        load.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("unsupported member", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Folder_loader_supports_schedule_wrapper_and_filename_invariant_order()
    {
        var directory = RuntimeBundleTestHelper.CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "entities"));
            Directory.CreateDirectory(Path.Combine(directory, "workflows"));
            Directory.CreateDirectory(Path.Combine(directory, "schedules"));
            Directory.CreateDirectory(Path.Combine(directory, "permissions"));
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.yaml"), FolderManifestYaml());
            await File.WriteAllTextAsync(Path.Combine(directory, "entities", "incident.yaml"), FolderEntityYaml());
            await File.WriteAllTextAsync(Path.Combine(directory, "workflows", "escalate.yaml"), FolderWorkflowYaml());
            var schedulePath = Path.Combine(directory, "schedules", "escalate.yaml");
            await File.WriteAllTextAsync(schedulePath, FolderScheduleYaml());
            await File.WriteAllTextAsync(Path.Combine(directory, "permissions", "roles.yaml"), FolderRolesYaml());

            var loader = new ErpBundleYamlLoader();
            var baseline = new ErpBundleCompiler().Compile(await loader.LoadDirectoryAsync(directory));
            File.Move(schedulePath, Path.Combine(directory, "schedules", "zz-escalate.yaml"));
            var renamed = new ErpBundleCompiler().Compile(await loader.LoadDirectoryAsync(directory));

            baseline.Schedules.Should().ContainSingle(schedule => schedule.Id == "escalate_due_incident");
            renamed.Schedules.Single().DefinitionHash.Should().Be(baseline.Schedules.Single().DefinitionHash);
            renamed.BundleHash.Should().Be(baseline.BundleHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validator_rejects_noncanonical_due_fields_and_non_triggerless_workflows()
    {
        var bundle = ScheduledBundle([]);
        bundle.Schedules[0] = CopySchedule(bundle.Schedules[0], dueField: "Due_At");
        bundle.Modules[0].Workflows[0] = CopyWorkflow(bundle.Modules[0].Workflows[0], triggerAction: "submit");

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error => error.Contains("exact canonical field id", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("must be triggerless", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_computed_due_and_filter_fields()
    {
        var bundle = ScheduledBundle([Equal("computed_amount", "decimal", 10m)]);
        bundle.Schedules[0] = CopySchedule(bundle.Schedules[0], dueField: "computed_due_at");

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error => error.Contains("materialized datetime", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("must be materialized on the same record", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_enforces_filter_operator_cardinality_bounds_and_typed_literals()
    {
        var bundle = ScheduledBundle(
        [
            new ErpScheduleFilter
            {
                Field = "status",
                Op = "exists",
                Value = new ErpTypedLiteral { Type = "string", Value = "open" }
            },
            new ErpScheduleFilter { Field = "attempts", Op = "in" },
            Equal("attempts", "string", "1"),
            new ErpScheduleFilter { Field = "title", Op = "gt", Value = new ErpTypedLiteral { Type = "string", Value = "a" } },
            new ErpScheduleFilter { Field = "status", Op = "matches", Value = new ErpTypedLiteral { Type = "string", Value = "open" } }
        ]);

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error => error.Contains("cannot declare value or values", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("between 1 and 16 typed values", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("literal type must be exactly 'integer'", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("requires an ordered materialized field", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("unsupported operator 'matches'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_enforces_bounded_filter_and_literal_list_sizes()
    {
        var filters = Enumerable.Range(0, ErpScheduleLimits.MaxFilters + 1)
            .Select(value => Equal("attempts", "integer", value))
            .ToList();
        filters.Add(new ErpScheduleFilter
        {
            Field = "attempts",
            Op = "in",
            Values = Enumerable.Range(0, ErpScheduleLimits.MaxValuesPerFilter + 1)
                .Select(value => new ErpTypedLiteral { Type = "integer", Value = value })
                .ToList()
        });

        var errors = new ErpBundleCompiler().Validate(ScheduledBundle(filters));

        errors.Should().Contain(error => error.Contains("cannot declare more than 8 filters", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("between 1 and 16 typed values", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_cross_entity_or_duplicated_schedule_workflow_targets()
    {
        var bundle = ScheduledBundle([]);
        bundle.Modules[0].Entities.Add(TaskEntity());
        bundle.Modules[0].Workflows[0] = CopyWorkflow(bundle.Modules[0].Workflows[0], entity: "task");
        bundle.Schedules.Add(CopySchedule(bundle.Schedules[0], id: "second_due_incident"));

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error => error.Contains("must target the same exact entity", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains("at most one schedule", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Migration_planner_marks_schedule_add_change_and_removal_as_warnings()
    {
        var compiler = new ErpBundleCompiler();
        var without = compiler.Compile(BaseBundle());
        var added = compiler.Compile(ScheduledBundle([Equal("status", "string", "open")]));
        var changed = compiler.Compile(ScheduledBundle([Equal("status", "string", "closed")]));

        var planner = new ErpBundleMigrationPlanner();
        var initialPlan = planner.PlanMigration(null, added);
        var addPlan = planner.PlanMigration(without, added);
        var changePlan = planner.PlanMigration(added, changed);
        var removePlan = planner.PlanMigration(changed, without);

        initialPlan.RiskLevel.Should().Be("warning");
        initialPlan.Operations.Should().Contain(operation => operation.Type == "add_schedule" && operation.Risk == "warning");
        addPlan.RiskLevel.Should().Be("warning");
        addPlan.Operations.Should().Contain(operation => operation.Type == "add_schedule" && operation.Risk == "warning");
        changePlan.Operations.Should().Contain(operation => operation.Type == "update_schedule" && operation.Risk == "warning");
        removePlan.Operations.Should().Contain(operation => operation.Type == "deprecate_schedule" && operation.Risk == "warning");
    }

    private static ErpBundle ScheduledBundle(List<ErpScheduleFilter> filters)
    {
        var bundle = BaseBundle();
        bundle.Schedules.Add(new ErpSchedule
        {
            Id = "escalate_due_incident",
            Entity = "incident",
            DueField = "due_at",
            Filters = filters,
            Workflow = "escalate_incident"
        });
        return bundle;
    }

    private static ErpBundle BaseBundle() => new()
    {
        Id = "schedule_test",
        Name = "Schedule test",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "operations",
                Name = "Operations",
                Entities = [IncidentEntity()],
                Workflows = [Workflow()]
            }
        ],
        Roles = new Dictionary<string, ErpRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = new ErpRole
            {
                DisplayName = "Owner",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            }
        }
    };

    private static ErpEntity IncidentEntity() => new()
    {
        Id = "incident",
        Name = "incidents",
        DisplayName = "Incidents",
        Display = new ErpEntityDisplay { Primary = "title" },
        Fields =
        [
            new ErpField { Id = "title", Name = "title", DisplayName = "Title", Type = "string", Required = true },
            new ErpField { Id = "due_at", Name = "due_at", DisplayName = "Due at", Type = "datetime" },
            new ErpField { Id = "status", Name = "status", DisplayName = "Status", Type = "enum", Options = ["open", "closed"] },
            new ErpField { Id = "attempts", Name = "attempts", DisplayName = "Attempts", Type = "integer" },
            new ErpField { Id = "amount", Name = "amount", DisplayName = "Amount", Type = "decimal" },
            new ErpField
            {
                Id = "computed_amount",
                Name = "computed_amount",
                DisplayName = "Computed amount",
                Type = "decimal",
                Computed = new ErpComputedField
                {
                    Op = "add",
                    Left = new ErpComputedOperand { Field = "amount" },
                    Right = new ErpComputedOperand { Value = 1m }
                }
            },
            new ErpField
            {
                Id = "computed_due_at",
                Name = "computed_due_at",
                DisplayName = "Computed due at",
                Type = "datetime",
                Computed = new ErpComputedField
                {
                    Op = "coalesce",
                    Left = new ErpComputedOperand { Field = "due_at" },
                    Right = new ErpComputedOperand { Field = "due_at" }
                }
            }
        ]
    };

    private static ErpEntity TaskEntity() => new()
    {
        Id = "task",
        Name = "tasks",
        DisplayName = "Tasks",
        Fields = [new ErpField { Id = "title", Name = "title", DisplayName = "Title", Type = "string" }]
    };

    private static ErpWorkflow Workflow() => new()
    {
        Id = "escalate_incident",
        Name = "Escalate incident",
        Entity = "incident",
        Steps = [new ErpWorkflowStep { Id = "record_escalation", Type = "audit_event", Event = "incident.escalated" }]
    };

    private static ErpScheduleFilter Equal(string field, string type, object value) => new()
    {
        Field = field,
        Op = "eq",
        Value = new ErpTypedLiteral { Type = type, Value = value }
    };

    private static ErpScheduleFilter Exists(string field) => new()
    {
        Field = field,
        Op = "exists"
    };

    private static ErpSchedule CopySchedule(
        ErpSchedule source,
        string? id = null,
        string? dueField = null) => new()
        {
            Id = id ?? source.Id,
            Entity = source.Entity,
            DueField = dueField ?? source.DueField,
            Filters = source.Filters,
            Workflow = source.Workflow
        };

    private static ErpWorkflow CopyWorkflow(
        ErpWorkflow source,
        string? entity = null,
        string? triggerAction = null) => new()
        {
            Id = source.Id,
            Name = source.Name,
            Entity = entity ?? source.Entity,
            States = source.States,
            Trigger = new ErpWorkflowTrigger { Action = triggerAction ?? source.Trigger.Action },
            StartGuards = source.StartGuards,
            Steps = source.Steps,
            OnRejected = source.OnRejected
        };

    private static string BundleYaml() =>
        """
        id: schedule_yaml
        name: Schedule YAML
        version: 1.0.0
        modules:
          - id: operations
            name: Operations
            entities:
              - id: incident
                name: incidents
                displayName: Incidents
                display:
                  primary: title
                fields:
                  - id: title
                    name: title
                    displayName: Title
                    type: string
                  - id: due_at
                    name: due_at
                    displayName: Due at
                    type: datetime
                  - id: status
                    name: status
                    displayName: Status
                    type: enum
                    options: [open, closed]
                  - id: attempts
                    name: attempts
                    displayName: Attempts
                    type: integer
            workflows:
              - id: escalate_incident
                name: Escalate incident
                entity: incident
                trigger: {}
                steps:
                  - id: record_escalation
                    type: audit_event
                    event: incident.escalated
        schedules:
          - id: escalate_due_incident
            entity: incident
            due_field: due_at
            filters:
              - field: status
                op: eq
                value:
                  type: string
                  value: open
              - field: attempts
                op: not_exists
            workflow: escalate_incident
        roles:
          owner:
            displayName: Owner
            grants:
              - action: "*"
                entity: "*"
        """;

    private static string FolderManifestYaml() =>
        """
        bundle:
          id: schedule_folder
          name: Schedule folder
          version: 1.0.0
        modules:
          - id: operations
            name: Operations
        """;

    private static string FolderEntityYaml() =>
        """
        module: operations
        entity:
          id: incident
          name: incidents
          displayName: Incidents
          display:
            primary: title
          fields:
            - id: title
              name: title
              displayName: Title
              type: string
            - id: due_at
              name: due_at
              displayName: Due at
              type: datetime
            - id: status
              name: status
              displayName: Status
              type: enum
              options: [open, closed]
        """;

    private static string FolderWorkflowYaml() =>
        """
        module: operations
        workflow:
          id: escalate_incident
          name: Escalate incident
          entity: incident
          trigger: {}
          steps:
            - id: record_escalation
              type: audit_event
              event: incident.escalated
        """;

    private static string FolderScheduleYaml() =>
        """
        schedule:
          id: escalate_due_incident
          entity: incident
          due_field: due_at
          filters:
            - field: status
              op: eq
              value:
                type: string
                value: open
          workflow: escalate_incident
        """;

    private static string FolderRolesYaml() =>
        """
        roles:
          owner:
            displayName: Owner
            grants:
              - action: "*"
                entity: "*"
        """;
}
