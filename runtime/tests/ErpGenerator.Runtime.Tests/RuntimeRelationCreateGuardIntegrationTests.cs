using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeRelationCreateGuardIntegrationTests
{
    [PostgresFact]
    public async Task Direct_and_action_creates_require_guarded_field_read_without_disclosing_target_state()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_relation_guard_acl_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "relation-guard-acl",
                "relation-guard-acl.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var records = new RuntimeRecordStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder())
                .ApplyCompiledBundleAsync(tenant, new ErpBundleCompiler().Compile(Bundle()));
            var owner = new RuntimeUserContext(
                1,
                "owner@relation-guard.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var restricted = new RuntimeUserContext(2, "restricted@relation-guard.test", "operator")
            {
                Roles = ["operator"]
            };

            var open = await CreateWorkOrderAsync(records, tenant, owner, $"Open {suffix}");
            var closed = await CreateWorkOrderAsync(records, tenant, owner, $"Closed {suffix}");
            var openId = Convert.ToInt64(open["id"]);
            var closedId = Convert.ToInt64(closed["id"]);
            await records.UpdateRecordAsync(
                tenant,
                owner,
                "work_orders",
                closedId,
                RuntimeJson.JsonObject(("status", "closed")));
            var missingId = Math.Max(openId, closedId) + 100_000;

            var visibleTarget = await records.GetRecordAsync(
                tenant,
                restricted,
                "work_orders",
                openId);
            visibleTarget.Should().NotBeNull();
            visibleTarget!.Should().ContainKey("title");
            visibleTarget.Should().NotContainKey("status",
                "the role can read the target row but its field allowlist hides the guarded field");

            var launcherIds = new List<long>();
            launcherIds.Add(Convert.ToInt64((await records.CreateRecordAsync(
                tenant,
                owner,
                "launchers",
                RuntimeJson.JsonObject(("label", "Open launcher"), ("work_order_id", openId))))["id"]));
            launcherIds.Add(Convert.ToInt64((await records.CreateRecordAsync(
                tenant,
                owner,
                "launchers",
                RuntimeJson.JsonObject(("label", "Closed launcher"), ("work_order_id", closedId))))["id"]));
            await using (var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "insert into app.launchers (label, work_order_id) values ('Missing launcher', @id) returning id";
                command.Parameters.AddWithValue("id", missingId);
                launcherIds.Add(Convert.ToInt64(await command.ExecuteScalarAsync()));
            }

            var directOutcomes = await Task.WhenAll(new[] { openId, closedId, missingId }.Select((targetId, index) =>
                CaptureExceptionAsync(() => records.CreateRecordAsync(
                    tenant,
                    restricted,
                    "completions",
                    RuntimeJson.JsonObject(("note", $"Restricted direct {index}"), ("work_order_id", targetId))))));
            directOutcomes.Should().OnlyContain(outcome => outcome is RuntimeRecordPermissionException);
            directOutcomes.Select(outcome => (outcome!.GetType(), outcome.Message))
                .Distinct().Should().ContainSingle();

            var actionOutcomes = await Task.WhenAll(launcherIds.Select(launcherId =>
                CaptureExceptionAsync(() => actions.ExecuteActionAsync(
                    tenant,
                    restricted,
                    "launchers",
                    launcherId,
                    "add_launcher_completion"))));
            actionOutcomes.Should().OnlyContain(outcome => outcome is RuntimeActionPermissionException);
            actionOutcomes.Select(outcome => (outcome!.GetType(), outcome.Message))
                .Distinct().Should().ContainSingle();
            actionOutcomes.Cast<RuntimeActionPermissionException>()
                .Should().OnlyContain(outcome =>
                    outcome.PermissionAction == "field.read" &&
                    outcome.Target == "work_order.status");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Direct_and_action_creates_serialize_with_referenced_status_changes_without_deadlock()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_relation_guard_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "relation-guard",
                "relation-guard.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var records = new RuntimeRecordStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder())
                .ApplyCompiledBundleAsync(tenant, new ErpBundleCompiler().Compile(Bundle()));
            var owner = new RuntimeUserContext(
                1,
                "owner@relation-guard.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };

            var first = await CreateWorkOrderAsync(records, tenant, owner, $"First {suffix}");
            var firstId = Convert.ToInt64(first["id"]);
            await records.CreateRecordAsync(
                tenant,
                owner,
                "completions",
                RuntimeJson.JsonObject(("note", "Direct while open"), ("work_order_id", firstId)));
            (await actions.ExecuteActionAsync(
                tenant,
                owner,
                "work_orders",
                firstId,
                "add_completion")).Success.Should().BeTrue();

            await FluentActions.Awaiting(() => actions.ExecuteActionAsync(
                    tenant,
                    owner,
                    "work_orders",
                    firstId,
                    "close_work_order"))
                .Should().ThrowAsync<RuntimeActionGuardRejectedException>();
            (await records.GetRecordAsync(tenant, "work_orders", firstId))!["status"]
                .Should().Be("open", "close must re-query after the committed unsigned completion");

            var closeFirst = await CreateWorkOrderAsync(records, tenant, owner, $"Close first {suffix}");
            var closeFirstId = Convert.ToInt64(closeFirst["id"]);
            (await actions.ExecuteActionAsync(
                tenant,
                owner,
                "work_orders",
                closeFirstId,
                "close_work_order")).Success.Should().BeTrue();
            await FluentActions.Awaiting(() => records.CreateRecordAsync(
                    tenant,
                    owner,
                    "completions",
                    RuntimeJson.JsonObject(("note", "Direct after close"), ("work_order_id", closeFirstId))))
                .Should().ThrowAsync<RuntimeRelationCreateGuardRejectedException>();
            await FluentActions.Awaiting(() => actions.ExecuteActionAsync(
                    tenant,
                    owner,
                    "work_orders",
                    closeFirstId,
                    "add_completion"))
                .Should().ThrowAsync<RuntimeRelationCreateGuardRejectedException>();

            await AssertNoClosedUnsignedCompletionAsync(factory, tenant);

            var racing = await CreateWorkOrderAsync(records, tenant, owner, $"Racing {suffix}");
            var racingId = Convert.ToInt64(racing["id"]);
            await using var blocker = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var lockCommand = blocker.CreateCommand())
            {
                lockCommand.Transaction = blockerTransaction;
                lockCommand.CommandText = "select id from app.work_orders where id = @id for update";
                lockCommand.Parameters.AddWithValue("id", racingId);
                (await lockCommand.ExecuteScalarAsync()).Should().Be(racingId);
            }

            var directRace = CaptureExceptionAsync(() => records.CreateRecordAsync(
                tenant,
                owner,
                "completions",
                RuntimeJson.JsonObject(("note", "Racing direct"), ("work_order_id", racingId))));
            var actionRace = CaptureExceptionAsync(() => actions.ExecuteActionAsync(
                tenant,
                owner,
                "work_orders",
                racingId,
                "add_completion"));

            await Task.Delay(150);
            directRace.IsCompleted.Should().BeFalse("the guarded direct create must wait on the referenced row lock");
            actionRace.IsCompleted.Should().BeFalse("the guarded action create must wait on the referenced row lock");

            await using (var closeCommand = blocker.CreateCommand())
            {
                closeCommand.Transaction = blockerTransaction;
                closeCommand.CommandText = "update app.work_orders set status = 'closed' where id = @id";
                closeCommand.Parameters.AddWithValue("id", racingId);
                (await closeCommand.ExecuteNonQueryAsync()).Should().Be(1);
            }

            await blockerTransaction.CommitAsync();
            var raceOutcomes = await Task.WhenAll(directRace, actionRace)
                .WaitAsync(TimeSpan.FromSeconds(10));
            raceOutcomes.Should().OnlyContain(outcome =>
                outcome is RuntimeRelationCreateGuardRejectedException,
                "both creates must observe the committed closed state and complete without a deadlock");

            var completions = await records.ListRecordsAsync(
                tenant,
                "completions",
                new RuntimeRecordQuery(PageSize: 100));
            completions.Records.Should().HaveCount(2);
            completions.Records.Should().NotContain(record =>
                Convert.ToInt64(record["work_order_id"]) == racingId);
            await AssertNoClosedUnsignedCompletionAsync(factory, tenant);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static async Task<Dictionary<string, object?>> CreateWorkOrderAsync(
        RuntimeRecordStore records,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext owner,
        string title) => await records.CreateRecordAsync(
        tenant,
        owner,
        "work_orders",
        RuntimeJson.JsonObject(("title", title), ("status", "open")));

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task AssertNoClosedUnsignedCompletionAsync(
        TenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from app.work_orders work_order
            join app.completions completion on completion.work_order_id = work_order.id
            where work_order.archived_at is null
              and completion.archived_at is null
              and work_order.status = 'closed'
              and completion.signoff_status = 'unsigned'
            """;
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);
    }

    private static ErpBundle Bundle() => new()
    {
        Id = "relation_guard_runtime",
        Name = "Relation guard runtime",
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
                        Id = "work_order",
                        Name = "work_orders",
                        DisplayName = "Work orders",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "title", Name = "title", DisplayName = "Title",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "status", Name = "status", DisplayName = "Status",
                                Type = "enum", Required = true, Options = ["open", "closed"]
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "completion",
                        Name = "completions",
                        DisplayName = "Completions",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "note", Name = "note", DisplayName = "Note",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "work_order", Name = "work_order_id", DisplayName = "Work order",
                                Type = "relation", Required = true, References = "work_order"
                            },
                            new ErpField
                            {
                                Id = "signoff_status", Name = "signoff_status", DisplayName = "Signoff status",
                                Type = "enum", Required = true, Options = ["unsigned", "signed"],
                                Lifecycle = new ErpFieldLifecycle
                                {
                                    Initial = "unsigned",
                                    Transitions = "direct"
                                }
                            }
                        ],
                        RelationCreateGuards =
                        [
                            new ErpRelationCreateGuard
                            {
                                RelationField = "work_order",
                                ReferencedField = "status",
                                Op = "eq",
                                Value = new ErpTypedLiteral { Type = "string", Value = "open" }
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "launcher",
                        Name = "launchers",
                        DisplayName = "Launchers",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "label", Name = "label", DisplayName = "Label",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "work_order", Name = "work_order_id", DisplayName = "Work order",
                                Type = "relation", Required = true, References = "work_order"
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
                Key = "add_completion",
                Label = "Add completion",
                Entity = "work_order",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Entity = "work_order", Action = "read" },
                        new ErpGrant { Entity = "completion", Action = "create" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "create_completion",
                        Type = "create_record",
                        Entity = "completion",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["note"] = new() { Value = "Created by action" },
                            ["work_order"] = new()
                            {
                                ValueFrom = new ErpActionValueFrom { Record = "id" }
                            }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "close_work_order",
                Label = "Close work order",
                Entity = "work_order",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Entity = "work_order", Action = "read" },
                        new ErpGrant { Entity = "work_order", Action = "update" },
                        new ErpGrant { Entity = "completion", Action = "read" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "find_unsigned_completions",
                        Type = "query_records",
                        Entity = "completion",
                        Where =
                        [
                            new ErpActionFilter
                            {
                                Field = "work_order",
                                Op = "eq",
                                ValueFrom = new ErpActionValueFrom { Record = "id" }
                            },
                            new ErpActionFilter
                            {
                                Field = "signoff_status",
                                Op = "eq",
                                Value = "unsigned"
                            }
                        ]
                    },
                    new ErpActionStep
                    {
                        Id = "require_all_signed",
                        Type = "guard",
                        Condition = new ErpActionCondition
                        {
                            Op = "eq",
                            Left = new ErpActionOperand { ResultCount = "find_unsigned_completions" },
                            Right = new ErpActionOperand { Value = 0 }
                        },
                        Error = "Every completion must be signed before close."
                    },
                    new ErpActionStep
                    {
                        Id = "mark_closed",
                        Type = "update_current_record",
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["status"] = new() { Value = "closed" }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "add_launcher_completion",
                Label = "Add launcher completion",
                Entity = "launcher",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Entity = "launcher", Action = "read" },
                        new ErpGrant { Entity = "completion", Action = "create" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "create_launcher_completion",
                        Type = "create_record",
                        Entity = "completion",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["note"] = new() { Value = "Created from launcher" },
                            ["work_order"] = new()
                            {
                                ValueFrom = new ErpActionValueFrom { Record = "work_order" }
                            }
                        }
                    }
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
            ["operator"] = new()
            {
                DisplayName = "Operator",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "launcher" },
                    new ErpGrant { Action = "read", Entity = "work_order" },
                    new ErpGrant
                    {
                        Scope = PermissionGrantScopes.Field,
                        Action = "read",
                        Entity = "work_order",
                        Target = "title"
                    },
                    new ErpGrant { Action = "create", Entity = "completion" },
                    new ErpGrant
                    {
                        Scope = PermissionGrantScopes.Action,
                        Action = "execute",
                        Entity = "launcher",
                        Target = "add_launcher_completion"
                    }
                ]
            }
        }
    };
}
