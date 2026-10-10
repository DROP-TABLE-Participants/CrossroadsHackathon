using System.Globalization;
using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeEntityIdentityIntegrationTests
{
    [PostgresFact]
    public async Task Canonical_ids_win_and_ambiguous_aliases_fail_closed_across_runtime_stores()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_entity_identity_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "entity-identity",
                "entity-identity.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissionService = new RuntimePermissionService(factory);
            var actions = new RuntimeActionStore(factory, permissionService);
            var workflows = new RuntimeWorkflowStore(factory, permissionService);
            var reports = new RuntimeReportStore(factory);
            await runtimeDatabase.ApplyCompiledBundleAsync(
                tenant,
                new ErpBundleCompiler().Compile(RuntimeBundle()));

            await CorruptEntityAliasesAsync(factory, tenant, "a_decoy", name: "work_item", tableName: "category");
            await CorruptEntityAliasesAsync(factory, tenant, "work_item", name: "category", tableName: "work_items");

            var actor = new RuntimeUserContext(501, "operator@example.test", "target_operator")
            {
                Roles = ["target_operator"]
            };
            var category = await records.CreateRecordAsync(
                tenant,
                actor,
                "category",
                RuntimeJson.JsonObject(("label", "Canonical category")));
            var categoryId = Convert.ToInt64(category["id"], CultureInfo.InvariantCulture);
            var workItemEntity = await records.GetEntityAsync(tenant, "work_item");
            workItemEntity.Should().NotBeNull();
            workItemEntity!.Id.Should().Be("work_item",
                "the exact canonical id must beat another entity's name alias");

            var workItem = await records.CreateRecordAsync(
                tenant,
                actor,
                "work_item",
                RuntimeJson.JsonObject(
                    ("label", "Identity target"),
                    ("status", "draft"),
                    ("category_id", categoryId)));
            var workItemId = Convert.ToInt64(workItem["id"], CultureInfo.InvariantCulture);
            await records.UpdateRecordAsync(
                tenant,
                actor,
                "work_item",
                workItemId,
                RuntimeJson.JsonObject(("label", "Identity target updated")));

            await actions.ExecuteActionAsync(
                tenant,
                actor,
                "work_item",
                workItemId,
                "create_category");
            (await CountAsync(factory, tenant, "categories")).Should().Be(2,
                "the action's canonical category id must not resolve through the decoy table alias");
            (await CountAsync(factory, tenant, "decoys")).Should().Be(0,
                "the colliding entity must never be mutated");

            var report = await reports.GetReportAsync(tenant, "items_by_category");
            var reportResult = await reports.ExecuteReportAsync(
                tenant,
                actor,
                report!,
                workItemEntity,
                new RuntimeReportQuery());
            reportResult.Rows.Should().ContainSingle();
            reportResult.Rows[0]["category.label"].Should().Be("Canonical category");
            reportResult.Rows[0]["item_count"].Should().Be(1L);

            var started = await workflows.StartActionAsync(
                tenant,
                actor,
                "work_item",
                workItemId,
                "submit");
            started.Instance.EntityId.Should().Be("work_item");
            started.Instance.Status.Should().Be("completed");
            (await workflows.GetInstanceAsync(tenant, actor, started.Instance.Id))
                .Should().NotBeNull("persisted workflow entity ids must be resolved exactly");
            var updated = await records.GetRecordAsync(tenant, actor, "work_item", workItemId);
            updated!["status"].Should().Be("submitted");
            updated["label"].Should().Be("Identity target updated");
            (await CountAsync(factory, tenant, "decoys")).Should().Be(0);

            await CorruptEntityAliasesAsync(factory, tenant, "work_item", name: "work_items", tableName: "work_items");
            await CorruptEntityAliasesAsync(factory, tenant, "a_decoy", name: "work_items", tableName: "category");

            (await records.GetEntityAsync(tenant, "work_items")).Should().BeNull(
                "an alias shared by multiple metadata rows must fail closed");
            (await actions.GetActionAsync(tenant, "work_items", "create_category")).Should().BeNull();
            await FluentActions.Awaiting(() => workflows.HasActionAsync(tenant, "work_items", "submit"))
                .Should().ThrowAsync<RuntimeEntityNotFoundException>();

            var canonicalAfterAliasCollision = await records.GetEntityAsync(tenant, "work_item");
            canonicalAfterAliasCollision!.Id.Should().Be("work_item",
                "an exact id remains authoritative even when other aliases are corrupt");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static async Task CorruptEntityAliasesAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string entityId,
        string name,
        string tableName)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.entity_definitions
            set name = @name, table_name = @tableName
            where id = @entityId
            """;
        command.Parameters.AddWithValue("entityId", entityId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("tableName", tableName);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task<long> CountAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string tableName)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from app.\"{tableName}\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static ErpBundle RuntimeBundle() => new()
    {
        Id = "runtime_entity_identity",
        Name = "Runtime entity identity",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "operations",
                Name = "Operations",
                Entities =
                [
                    Entity("a_decoy", "decoys",
                    [
                        Field("label", "string", required: true),
                        Field("status", "string")
                    ]),
                    Entity("category", "categories",
                    [
                        Field("label", "string", required: true)
                    ]),
                    Entity("work_item", "work_items",
                    [
                        Field("label", "string", required: true),
                        new ErpField
                        {
                            Id = "status",
                            Name = "status",
                            DisplayName = "Status",
                            Type = "enum",
                            Required = true,
                            Options = ["draft", "submitted"]
                        },
                        new ErpField
                        {
                            Id = "category",
                            Name = "category_id",
                            DisplayName = "Category",
                            Type = "relation",
                            Required = true,
                            References = "category"
                        }
                    ])
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = "submit_work_item",
                        Name = "Submit work item",
                        Entity = "work_item",
                        Trigger = new ErpWorkflowTrigger { Action = "submit" },
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "mark_submitted",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["status"] = "submitted" }
                            },
                            new ErpWorkflowStep
                            {
                                Id = "audit_submitted",
                                Type = "audit_event",
                                Event = "work_item.submitted",
                                Message = "Work item submitted."
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
                Key = "create_category",
                Label = "Create category",
                Entity = "work_item",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Action = "read", Entity = "work_item" },
                        new ErpGrant { Action = "create", Entity = "category" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "create_generated_category",
                        Type = "create_record",
                        Entity = "category",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["label"] = new() { Value = "Generated category" }
                        }
                    }
                ]
            }
        ],
        Reports =
        [
            new ErpReport
            {
                Key = "items_by_category",
                Label = "Items by category",
                Source = "work_item",
                Joins =
                [
                    new ErpReportJoin
                    {
                        Alias = "category",
                        From = "source",
                        Relation = "category",
                        Type = "inner"
                    }
                ],
                GroupBy =
                [
                    new ErpReportGroupBy { Field = "category.label", Label = "Category" }
                ],
                Metrics =
                [
                    new ErpReportMetric { Key = "item_count", Label = "Items", Op = "count" }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["owner"] = new()
            {
                DisplayName = "Owner",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            },
            ["target_operator"] = new()
            {
                DisplayName = "Target operator",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "work_item" },
                    new ErpGrant { Action = "create", Entity = "work_item" },
                    new ErpGrant { Action = "update", Entity = "work_item" },
                    new ErpGrant { Action = "start_workflow", Entity = "work_item" },
                    new ErpGrant { Action = "read_task", Entity = "work_item" },
                    new ErpGrant { Action = "read", Entity = "category" },
                    new ErpGrant { Action = "create", Entity = "category" },
                    new ErpGrant
                    {
                        Scope = PermissionGrantScopes.Action,
                        Action = "execute",
                        Entity = "work_item",
                        Target = "create_category"
                    }
                ]
            }
        }
    };

    private static ErpEntity Entity(string id, string name, List<ErpField> fields) => new()
    {
        Id = id,
        Name = name,
        DisplayName = id.Replace('_', ' '),
        Fields = fields
    };

    private static ErpField Field(string id, string type, bool required = false) => new()
    {
        Id = id,
        Name = id,
        DisplayName = id.Replace('_', ' '),
        Type = type,
        Required = required
    };
}
