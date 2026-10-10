using System.Text.Json;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeLifecyclePolicyIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [PostgresFact]
    public async Task Lifecycle_defaults_and_named_action_transitions_are_enforced_across_runtime_writers()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_lifecycle_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "lifecycle",
                "lifecycle.erpgenerator.example",
                database,
                TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            var workflows = new RuntimeWorkflowStore(factory);
            var compiled = CompileBundle();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = new RuntimeUserContext(
                1,
                "owner@lifecycle.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var manager = new RuntimeUserContext(2, "manager@lifecycle.test", "manager")
            {
                Roles = ["manager"]
            };

            var created = await records.CreateRecordAsync(
                tenant,
                owner,
                "formulas",
                RuntimeJson.JsonObject(("name", $"Formula {suffix}")));
            created["status"].Should().Be("Draft");
            var formulaId = Convert.ToInt64(created["id"]);

            var suppliedCreate = () => records.CreateRecordAsync(
                tenant,
                owner,
                "formulas",
                RuntimeJson.JsonObject(("name", "Smuggled formula"), ("status", "Approved")));
            var suppliedCreateError = await suppliedCreate.Should().ThrowAsync<RuntimeRecordValidationException>();
            suppliedCreateError.Which.Errors.Should().Contain(error =>
                error.Field == "status" &&
                error.Message.Contains("initialized by the runtime", StringComparison.OrdinalIgnoreCase));

            var directTransition = () => records.UpdateRecordAsync(
                tenant,
                owner,
                "formulas",
                formulaId,
                RuntimeJson.JsonObject(("status", "Approved")));
            var directTransitionError = await directTransition.Should().ThrowAsync<RuntimeRecordValidationException>();
            directTransitionError.Which.Errors.Should().Contain(error =>
                error.Field == "status" &&
                error.Message.Contains("authorized named action", StringComparison.OrdinalIgnoreCase));
            (await records.GetRecordAsync(tenant, "formulas", formulaId))!["status"].Should().Be("Draft");

            var directLifecycle = await records.CreateRecordAsync(
                tenant,
                owner,
                "direct_items",
                RuntimeJson.JsonObject(("name", "Direct lifecycle item")));
            directLifecycle["status"].Should().Be("Draft");
            var directLifecycleId = Convert.ToInt64(directLifecycle["id"]);
            var directLifecycleUpdated = await records.UpdateRecordAsync(
                tenant,
                owner,
                "direct_items",
                directLifecycleId,
                RuntimeJson.JsonObject(("status", "Approved")));
            directLifecycleUpdated!["status"].Should().Be("Approved");

            (await actions.ExecuteActionAsync(
                tenant,
                manager,
                "formulas",
                formulaId,
                "approve_formula")).Success.Should().BeTrue();
            (await records.GetRecordAsync(tenant, "formulas", formulaId))!["status"].Should().Be("Approved");

            var createBatch = await actions.ExecuteActionAsync(
                tenant,
                manager,
                "formulas",
                formulaId,
                "create_batch");
            createBatch.Success.Should().BeTrue();
            createBatch.Created.Should().NotBeNull();
            var batch = await records.GetRecordAsync(tenant, "batches", createBatch.Created!.Id);
            batch!["status"].Should().Be("Draft");
            batch["formula_id"].Should().Be(formulaId);
            (await actions.ExecuteActionAsync(
                tenant,
                manager,
                "batches",
                createBatch.Created.Id,
                "approve_batch")).Success.Should().BeTrue();
            (await records.GetRecordAsync(tenant, "batches", createBatch.Created.Id))!["status"]
                .Should().Be("Approved");

            var seedRunner = new RuntimeSeedRunner(records);
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "lifecycle-seed", Label = "Lifecycle seed" },
                Records =
                [
                    new BundleSeedRecord
                    {
                        Ref = "seeded-formula",
                        Entity = "formula",
                        Values = new Dictionary<string, object?> { ["name"] = "Seeded formula" }
                    }
                ]
            };
            var seeded = await seedRunner.ApplySeedAsync(tenant, owner, seed);
            var seededFormula = await records.GetRecordAsync(
                tenant,
                "formulas",
                seeded.Refs["seeded-formula"].Id);
            seededFormula!["status"].Should().Be("Draft");

            var countBeforeInvalidSeed = (await records.ListRecordsAsync(
                tenant,
                "formulas",
                new RuntimeRecordQuery(PageSize: 100))).Total;
            var invalidSeed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "invalid-lifecycle-seed", Label = "Invalid lifecycle seed" },
                Records =
                [
                    new BundleSeedRecord
                    {
                        Ref = "released-formula",
                        Entity = "formula",
                        Values = new Dictionary<string, object?>
                        {
                            ["name"] = "Released formula",
                            ["status"] = "Approved"
                        }
                    }
                ]
            };
            var smuggledSeed = () => seedRunner.ApplySeedAsync(tenant, owner, invalidSeed);
            await smuggledSeed.Should().ThrowAsync<RuntimeSeedException>()
                .WithMessage("*cannot set server-initialized lifecycle field 'status'*");
            (await records.ListRecordsAsync(tenant, "formulas", new RuntimeRecordQuery(PageSize: 100)))
                .Total.Should().Be(countBeforeInvalidSeed);

            await PersistTamperedWorkflowAsync(factory, tenant, compiled);
            var workflowTransition = () => workflows.StartActionAsync(
                tenant,
                owner,
                "formulas",
                formulaId,
                "audit_formula");
            await workflowTransition.Should().ThrowAsync<RuntimeWorkflowValidationException>()
                .WithMessage("*cannot transition named-action lifecycle field 'formulas.status'*");
            (await records.GetRecordAsync(tenant, "formulas", formulaId))!["status"].Should().Be("Approved");

            var formulaCountBeforeTamperedAction = (await records.ListRecordsAsync(
                tenant,
                "formulas",
                new RuntimeRecordQuery(PageSize: 100))).Total;
            await PersistTamperedCreateActionAsync(factory, tenant, compiled);
            (await actions.ListRecordActionsAsync(tenant, owner, "formulas", formulaId))
                .Should().NotContain(action => action.Key == "approve_formula");
            var tamperedAction = () => actions.ExecuteActionAsync(
                tenant,
                owner,
                "formulas",
                formulaId,
                "approve_formula");
            await tamperedAction.Should().ThrowAsync<RuntimeActionDefinitionException>()
                .WithMessage("*supplies server-initialized lifecycle field 'formula.status' during create*");
            (await records.ListRecordsAsync(tenant, "formulas", new RuntimeRecordQuery(PageSize: 100)))
                .Total.Should().Be(formulaCountBeforeTamperedAction);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Persisted_lifecycle_metadata_must_match_the_active_compiled_definition_across_mutation_paths()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_lifecycle_integrity_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "lifecycle-integrity",
                "lifecycle-integrity.erpgenerator.example",
                database,
                TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            var workflows = new RuntimeWorkflowStore(factory);
            var compiled = CompileBundle();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = new RuntimeUserContext(1, "owner@lifecycle.test", RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var manager = new RuntimeUserContext(2, "manager@lifecycle.test", "manager")
            {
                Roles = ["manager"]
            };
            var direct = await records.CreateRecordAsync(
                tenant,
                owner,
                "direct_items",
                RuntimeJson.JsonObject(("name", "Integrity direct")));
            var formula = await records.CreateRecordAsync(
                tenant,
                owner,
                "formulas",
                RuntimeJson.JsonObject(("name", "Integrity formula")));
            var directId = Convert.ToInt64(direct["id"]);
            var formulaId = Convert.ToInt64(formula["id"]);

            var directMetadata = await ReadFieldMetadataAsync(factory, tenant, "direct_item.status");
            await TamperFieldLifecycleAsync(factory, tenant, "direct_item.status", "remove");
            var removed = () => records.UpdateRecordAsync(
                tenant,
                owner,
                "direct_items",
                directId,
                RuntimeJson.JsonObject(("status", "Approved")));
            await removed.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not match the active compiled bundle definition*");
            await RestoreFieldMetadataAsync(factory, tenant, "direct_item.status", directMetadata);

            var formulaMetadata = await ReadFieldMetadataAsync(factory, tenant, "formula.status");
            await TamperFieldLifecycleAsync(factory, tenant, "formula.status", "initial");
            var changedInitial = () => actions.ExecuteActionAsync(
                tenant,
                manager,
                "formulas",
                formulaId,
                "approve_formula");
            await changedInitial.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not match the active compiled bundle definition*");
            await RestoreFieldMetadataAsync(factory, tenant, "formula.status", formulaMetadata);

            await TamperFieldLifecycleAsync(factory, tenant, "formula.status", "transitions");
            var changedTransitions = () => workflows.StartActionAsync(
                tenant,
                owner,
                "formulas",
                formulaId,
                "audit_formula");
            await changedTransitions.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not match the active compiled bundle definition*");
            await RestoreFieldMetadataAsync(factory, tenant, "formula.status", formulaMetadata);

            (await records.GetRecordAsync(tenant, "direct_items", directId))!["status"].Should().Be("Draft");
            (await records.GetRecordAsync(tenant, "formulas", formulaId))!["status"].Should().Be("Draft");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task V3_acceptance_executes_direct_transitions_and_observes_the_named_action_lifecycle_boundary()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_lifecycle_v3_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "lifecycle-v3",
                "lifecycle-v3.erpgenerator.example",
                database,
                TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = CompileBundle();
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
                Seed = new BundleSeed { Key = "none", Label = "Empty lifecycle fixture" }
            };
            var seedHash = BundleSeedContractHasher.ComputeHash(seed);
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "lifecycle_boundary",
                    Label = "Lifecycle transitions execute through their exact policy boundary"
                },
                Setup = new BundleAcceptanceSetup { Seed = seed.Seed.Key, SeedHash = seedHash },
                Actors = [new BundleAcceptanceActor { Id = "editor", Roles = ["editor"] }],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_direct",
                        Type = "create_record",
                        Entity = "direct_item",
                        AsRole = "editor",
                        AsActor = "editor",
                        Values = new Dictionary<string, object?> { ["name"] = $"Direct {suffix}" }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "update_direct",
                        Type = "update_record",
                        Entity = "direct_item",
                        Record = new BundleRecordSelector { Ref = "create_direct" },
                        AsRole = "editor",
                        AsActor = "editor",
                        Set = new Dictionary<string, object?> { ["status"] = "Approved" }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "create_named",
                        Type = "create_record",
                        Entity = "formula",
                        AsRole = "editor",
                        AsActor = "editor",
                        Values = new Dictionary<string, object?> { ["name"] = $"Named {suffix}" }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "deny_named_direct",
                        Type = "update_record",
                        Entity = "formula",
                        Record = new BundleRecordSelector { Ref = "create_named" },
                        AsRole = "editor",
                        AsActor = "editor",
                        Set = new Dictionary<string, object?> { ["status"] = "Approved" },
                        Expected = new BundleAcceptanceExpectedOutcome
                        {
                            Outcome = BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                            Code = BundleAcceptanceExpectedOutcomeCodes.LifecycleNamedActionRequired,
                            Target = "status"
                        }
                    }
                ],
                Assertions =
                [
                    FieldAssertion("direct_initial", "create_direct", "direct_item", "create_direct", "Draft"),
                    FieldAssertion("direct_updated", "update_direct", "direct_item", "create_direct", "Approved"),
                    FieldAssertion("named_initial", "create_named", "formula", "create_named", "Draft"),
                    FieldAssertion("named_unchanged", "deny_named_direct", "formula", "create_named", "Draft")
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
            runtimeTest.StepObservations.Single(item => item.StepId == "update_direct")
                .ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.Success);
            runtimeTest.StepObservations.Single(item => item.StepId == "deny_named_direct")
                .Should().Match<RuntimeBundleTestStepObservation>(item =>
                    item.Status == RuntimeBundleTestStepObservationStatuses.Passed &&
                    item.ActualOutcome == BundleAcceptanceExpectedOutcomes.BusinessRuleRejected &&
                    item.ActualCode == BundleAcceptanceExpectedOutcomeCodes.LifecycleNamedActionRequired &&
                    item.ActualTarget == "status");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static BundleAcceptanceAssertion FieldAssertion(
        string id,
        string afterStep,
        string entity,
        string recordRef,
        string value) =>
        new()
        {
            Id = id,
            AfterStep = afterStep,
            Type = "record_field_equals",
            Entity = entity,
            Record = new BundleRecordSelector { Ref = recordRef },
            Field = "status",
            Value = value
        };

    private static async Task<string> ReadFieldMetadataAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string fieldId)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select metadata::text from _runtime.field_definitions where id = @id";
        command.Parameters.AddWithValue("id", fieldId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task TamperFieldLifecycleAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string fieldId,
        string mutation)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = mutation switch
        {
            "remove" => "update _runtime.field_definitions set metadata = metadata - 'Lifecycle' - 'lifecycle' where id = @id",
            "initial" => "update _runtime.field_definitions set metadata = jsonb_set(metadata, '{Lifecycle,Initial}', '\"Approved\"'::jsonb, false) where id = @id",
            "transitions" => "update _runtime.field_definitions set metadata = jsonb_set(metadata, '{Lifecycle,Transitions}', '\"direct\"'::jsonb, false) where id = @id",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };
        command.Parameters.AddWithValue("id", fieldId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task RestoreFieldMetadataAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string fieldId,
        string metadata)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update _runtime.field_definitions set metadata = @metadata where id = @id";
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = metadata;
        command.Parameters.AddWithValue("id", fieldId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task PersistTamperedWorkflowAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        CompiledBundle compiled)
    {
        var workflow = compiled.Workflows.Single(item => item.Id == "formula_audit");
        var original = workflow.Steps.Single();
        var tampered = workflow with
        {
            Steps =
            [
                original with
                {
                    Type = "update_record",
                    Set = new Dictionary<string, string> { ["status"] = "Draft" },
                    Event = null,
                    Message = null
                }
            ]
        };

        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update _runtime.workflow_definitions set metadata = @metadata where id = @id";
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(tampered, JsonOptions);
        command.Parameters.AddWithValue("id", workflow.Id);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task PersistTamperedCreateActionAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        CompiledBundle compiled)
    {
        var action = compiled.Actions.Single(item => item.Key == "approve_formula");
        var original = action.Steps.Single();
        var tampered = action with
        {
            Permissions =
            [
                new ActionPermissionRequirementDefinition("read", "formula"),
                new ActionPermissionRequirementDefinition("effect_create", "formula")
            ],
            Steps =
            [
                original with
                {
                    Type = "create_record",
                    Entity = "formula",
                    Values = new Dictionary<string, ActionValueDefinition>
                    {
                        ["name"] = new("Tampered copy", null, null, null),
                        ["status"] = new("Approved", null, null, null)
                    },
                    Set = new Dictionary<string, ActionValueDefinition>()
                }
            ]
        };

        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update _runtime.action_definitions set metadata = @metadata where id = @id";
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(tampered, JsonOptions);
        command.Parameters.AddWithValue("id", action.Key);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static CompiledBundle CompileBundle()
    {
        try
        {
            return new ErpBundleCompiler().Compile(Bundle());
        }
        catch (BundleCompilationException exception)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, exception.Errors), exception);
        }
    }

    private static ErpBundle Bundle()
    {
        static ErpField StatusField(string transitions = "named_action") => new()
        {
            Id = "status",
            Name = "status",
            DisplayName = "Status",
            Type = "enum",
            Required = true,
            Options = ["Draft", "Approved", "Released"],
            Lifecycle = new ErpFieldLifecycle { Initial = "Draft", Transitions = transitions }
        };

        return new ErpBundle
        {
            Id = "runtime_lifecycle_bundle",
            Name = "Runtime lifecycle bundle",
            Version = "1.0.0",
            Modules =
            [
                new ErpModule
                {
                    Id = "erp",
                    Name = "Production",
                    Entities =
                    [
                        new ErpEntity
                        {
                            Id = "formula",
                            Name = "formulas",
                            DisplayName = "Formulas",
                            Display = new ErpEntityDisplay { Primary = "name" },
                            Fields =
                            [
                                new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                                StatusField()
                            ]
                        },
                        new ErpEntity
                        {
                            Id = "batch",
                            Name = "batches",
                            DisplayName = "Batches",
                            Display = new ErpEntityDisplay { Primary = "name" },
                            Fields =
                            [
                                new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                                new ErpField { Id = "formula", Name = "formula_id", DisplayName = "Formula", Type = "relation", Required = true, References = "formula" },
                                StatusField()
                            ]
                        },
                        new ErpEntity
                        {
                            Id = "direct_item",
                            Name = "direct_items",
                            DisplayName = "Direct lifecycle items",
                            Display = new ErpEntityDisplay { Primary = "name" },
                            Fields =
                            [
                                new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                                StatusField("direct")
                            ]
                        }
                    ],
                    Workflows =
                    [
                        new ErpWorkflow
                        {
                            Id = "formula_audit",
                            Name = "Formula audit",
                            Entity = "formula",
                            Trigger = new ErpWorkflowTrigger { Action = "audit_formula" },
                            Steps =
                            [
                                new ErpWorkflowStep
                                {
                                    Id = "record_audit",
                                    Type = "audit_event",
                                    Event = "formula.audited",
                                    Message = "Formula audited."
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
                    Key = "approve_formula",
                    Label = "Approve formula",
                    Entity = "formula",
                    Type = "record_action",
                    Requires = new ErpActionRequires
                    {
                        Permissions =
                        [
                            new ErpGrant { Entity = "formula", Action = "read" },
                            new ErpGrant { Entity = "formula", Action = "effect_update" }
                        ]
                    },
                    Steps =
                    [
                        new ErpActionStep
                        {
                            Id = "mark_approved",
                            Type = "update_current_record",
                            Set = new Dictionary<string, ErpActionValue>
                            {
                                ["status"] = new() { Value = "Approved" }
                            }
                        }
                    ]
                },
                new ErpAction
                {
                    Key = "create_batch",
                    Label = "Create batch",
                    Entity = "formula",
                    Type = "record_action",
                    Requires = new ErpActionRequires
                    {
                        Permissions =
                        [
                            new ErpGrant { Entity = "formula", Action = "read" },
                            new ErpGrant { Entity = "batch", Action = "effect_create" }
                        ]
                    },
                    Steps =
                    [
                        new ErpActionStep
                        {
                            Id = "create_batch_record",
                            Type = "create_record",
                            Entity = "batch",
                            Values = new Dictionary<string, ErpActionValue>
                            {
                                ["name"] = new() { Value = "Generated batch" },
                                ["formula"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } }
                            }
                        },
                        new ErpActionStep
                        {
                            Id = "return_batch_record",
                            Type = "return_result",
                            Return = new ErpActionReturn
                            {
                                Step = "create_batch_record",
                                Entity = "batch",
                                Message = "Batch created."
                            }
                        }
                    ]
                },
                new ErpAction
                {
                    Key = "approve_batch",
                    Label = "Approve batch",
                    Entity = "batch",
                    Type = "record_action",
                    Requires = new ErpActionRequires
                    {
                        Permissions =
                        [
                            new ErpGrant { Entity = "batch", Action = "read" },
                            new ErpGrant { Entity = "batch", Action = "effect_update" }
                        ]
                    },
                    Steps =
                    [
                        new ErpActionStep
                        {
                            Id = "mark_batch_approved",
                            Type = "update_current_record",
                            Set = new Dictionary<string, ErpActionValue>
                            {
                                ["status"] = new() { Value = "Approved" }
                            }
                        }
                    ]
                }
            ],
            Roles = new Dictionary<string, ErpRole>
            {
                ["manager"] = new()
                {
                    DisplayName = "Manager",
                    Grants =
                    [
                        new ErpGrant { Entity = "formula", Action = "read" },
                        new ErpGrant { Entity = "formula", Action = "effect_update" },
                        new ErpGrant { Entity = "formula", Scope = "field", Target = "status", Action = "effect_write" },
                        new ErpGrant { Entity = "formula", Scope = "action", Target = "approve_formula", Action = "execute" },
                        new ErpGrant { Entity = "batch", Action = "effect_create" },
                        new ErpGrant { Entity = "batch", Scope = "field", Target = "name", Action = "effect_write" },
                        new ErpGrant { Entity = "batch", Scope = "field", Target = "formula", Action = "effect_write" },
                        new ErpGrant { Entity = "formula", Scope = "action", Target = "create_batch", Action = "execute" },
                        new ErpGrant { Entity = "batch", Action = "read" },
                        new ErpGrant { Entity = "batch", Action = "effect_update" },
                        new ErpGrant { Entity = "batch", Scope = "field", Target = "status", Action = "effect_write" },
                        new ErpGrant { Entity = "batch", Scope = "action", Target = "approve_batch", Action = "execute" }
                    ]
                },
                ["editor"] = new()
                {
                    DisplayName = "Lifecycle test editor",
                    Grants =
                    [
                        new ErpGrant { Entity = "formula", Action = "create" },
                        new ErpGrant { Entity = "formula", Action = "read" },
                        new ErpGrant { Entity = "formula", Action = "update" },
                        new ErpGrant { Entity = "direct_item", Action = "create" },
                        new ErpGrant { Entity = "direct_item", Action = "read" },
                        new ErpGrant { Entity = "direct_item", Action = "update" }
                    ]
                }
            }
        };
    }
}
