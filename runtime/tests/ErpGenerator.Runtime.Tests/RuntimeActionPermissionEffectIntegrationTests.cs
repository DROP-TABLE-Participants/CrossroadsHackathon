using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeActionPermissionEffectIntegrationTests(
    RuntimeActionPermissionEffectDatabaseFixture fixture) :
    IClassFixture<RuntimeActionPermissionEffectDatabaseFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [PostgresFact]
    public async Task Runtime_rejects_tampered_action_metadata_with_underdeclared_create_effect()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.PersistActionAsync(fixture.Action);
        var before = await fixture.InvoiceCountAsync();

        var validVisible = await fixture.Actions.ListRecordActionsAsync(
            fixture.Tenant,
            fixture.Billing,
            "customers",
            fixture.CustomerId);
        validVisible.Should().Contain(action => action.Key == fixture.Action.Key);
        (await fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Billing,
            "customers",
            fixture.CustomerId,
            fixture.Action.Key)).Success.Should().BeTrue();
        (await fixture.InvoiceCountAsync()).Should().Be(before + 1);

        var tampered = fixture.Action with
        {
            Permissions = [new ActionPermissionRequirementDefinition("read", "customer")]
        };
        await fixture.PersistActionAsync(tampered);

        await AssertInvalidDefinitionAsync();
        (await fixture.InvoiceCountAsync()).Should().Be(before + 1);
    }

    [PostgresFact]
    public async Task Effect_update_action_succeeds_without_direct_update_while_direct_patch_is_denied()
    {
        if (!fixture.Available)
        {
            return;
        }

        var visible = await fixture.Actions.ListRecordActionsAsync(
            fixture.Tenant,
            fixture.EffectOperator,
            "customers",
            fixture.CustomerId);
        visible.Should().Contain(action => action.Key == fixture.EffectAction.Key);

        (await fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.EffectOperator,
            "customers",
            fixture.CustomerId,
            fixture.EffectAction.Key)).Success.Should().BeTrue();
        (await fixture.Records.GetRecordAsync(fixture.Tenant, "customers", fixture.CustomerId))!["name"]
            .Should().Be("Effect action updated");

        var directPatch = () => fixture.Records.UpdateRecordAsync(
            fixture.Tenant,
            fixture.EffectOperator,
            "customers",
            fixture.CustomerId,
            RuntimeJson.JsonObject(("name", "direct patch must fail")));
        await directPatch.Should().ThrowAsync<RuntimeRecordPermissionException>();
    }

    [PostgresFact]
    public async Task Effect_create_action_succeeds_without_direct_create_while_direct_post_is_denied()
    {
        if (!fixture.Available)
        {
            return;
        }

        var before = await fixture.InvoiceCountAsync();
        (await fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.EffectOperator,
            "customers",
            fixture.CustomerId,
            fixture.EffectCreateAction.Key)).Success.Should().BeTrue();
        (await fixture.InvoiceCountAsync()).Should().Be(before + 1);

        var directPost = () => fixture.Records.CreateRecordAsync(
            fixture.Tenant,
            fixture.EffectOperator,
            "invoices",
            RuntimeJson.JsonObject(("customer", fixture.CustomerId), ("amount", 99m)));
        await directPost.Should().ThrowAsync<RuntimeRecordPermissionException>();
        (await fixture.InvoiceCountAsync()).Should().Be(before + 1);
    }

    [PostgresTheory]
    [InlineData("missing_effect", "effect_update", "customer")]
    [InlineData("missing_action", "named_action.execute", "effect_update_customer")]
    [InlineData("missing_effect_write", "field.effect_write", "customers.name")]
    public async Task Effect_action_denies_missing_entity_action_or_field_authority(
        string role,
        string permissionAction,
        string target)
    {
        if (!fixture.Available)
        {
            return;
        }

        var user = fixture.EffectDenialUsers[role];
        var before = await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "customers",
            fixture.CustomerId);
        var visible = await fixture.Actions.ListRecordActionsAsync(
            fixture.Tenant,
            user,
            "customers",
            fixture.CustomerId);
        visible.Should().NotContain(action => action.Key == fixture.EffectAction.Key);

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            user,
            "customers",
            fixture.CustomerId,
            fixture.EffectAction.Key);
        var denied = await execute.Should().ThrowAsync<RuntimeActionPermissionException>();
        denied.Which.PermissionAction.Should().Be(permissionAction);
        denied.Which.Target.Should().Be(target);
        var after = await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "customers",
            fixture.CustomerId);
        after!["name"].Should().Be(before!["name"]);
    }

    [PostgresFact]
    public async Task Runtime_rejects_tampered_update_current_when_metadata_source_differs_from_stored_source()
    {
        if (!fixture.Available)
        {
            return;
        }

        var before = await fixture.Records.GetRecordAsync(fixture.Tenant, "customers", fixture.CustomerId);
        var sourceName = before!["name"];
        var decoyStep = fixture.Action.Steps.Single() with
        {
            Id = "tampered_update_current",
            Type = "update_current_record",
            Entity = "invoice",
            Set = new Dictionary<string, ActionValueDefinition>
            {
                ["name"] = new("should-not-be-written", null, null, null)
            }
        };
        var tampered = fixture.Action with
        {
            Entity = "invoice",
            Permissions = [new ActionPermissionRequirementDefinition("update", "invoice")],
            Steps = [decoyStep]
        };
        await fixture.PersistActionAsync(tampered);

        await AssertInvalidDefinitionAsync();
        (await fixture.Records.GetRecordAsync(fixture.Tenant, "customers", fixture.CustomerId))!["name"]
            .Should().Be(sourceName);
    }

    [PostgresFact]
    public async Task Empty_steps_and_permissions_deny_listing_and_execution()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.PersistActionAsync(fixture.Action with { Permissions = [] });
        await AssertInvalidDefinitionAsync();

        await fixture.PersistActionAsync(fixture.Action with { Steps = [] });
        await AssertInvalidDefinitionAsync();
    }

    [PostgresFact]
    public async Task Null_permission_metadata_denies_listing_and_execution()
    {
        if (!fixture.Available)
        {
            return;
        }

        var nullCollection = JsonSerializer.SerializeToNode(fixture.Action, JsonOptions)!.AsObject();
        nullCollection["permissions"] = null;
        await fixture.PersistMetadataJsonAsync(nullCollection.ToJsonString(JsonOptions));
        await AssertInvalidDefinitionAsync();

        var nullEntry = JsonSerializer.SerializeToNode(fixture.Action, JsonOptions)!.AsObject();
        nullEntry["permissions"] = new JsonArray { null };
        await fixture.PersistMetadataJsonAsync(nullEntry.ToJsonString(JsonOptions));
        await AssertInvalidDefinitionAsync();
    }

    [PostgresFact]
    public async Task Malformed_action_json_is_skipped_by_listing_without_throwing()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.PersistMetadataJsonAsync(
            """
            {
              "key": [],
              "label": "Malformed action",
              "entity": "customer",
              "type": "record_action"
            }
            """);

        IReadOnlyList<RuntimeRecordAction>? visible = null;
        var list = async () => visible = await fixture.Actions.ListRecordActionsAsync(
            fixture.Tenant,
            fixture.Owner,
            "customers",
            fixture.CustomerId);

        await list.Should().NotThrowAsync();
        visible.Should().NotContain(action => action.Key == fixture.Action.Key);
    }

    [PostgresTheory]
    [InlineData("*", "invoice")]
    [InlineData("create", "*")]
    public async Task Wildcard_action_or_entity_requirements_deny_listing_and_execution(
        string requiredAction,
        string requiredEntity)
    {
        if (!fixture.Available)
        {
            return;
        }

        var before = await fixture.InvoiceCountAsync();
        await fixture.PersistActionAsync(fixture.Action with
        {
            Permissions = [new ActionPermissionRequirementDefinition(requiredAction, requiredEntity)]
        });

        await AssertInvalidDefinitionAsync();
        (await fixture.InvoiceCountAsync()).Should().Be(before);
    }

    [PostgresTheory]
    [InlineData("update", "customer")]
    [InlineData("effect_create", "invoice")]
    [InlineData("effect_update", "customer")]
    public async Task Ambiguous_or_orphan_persisted_effect_metadata_denies_listing_and_execution(
        string effectAction,
        string effectEntity)
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.PersistActionAsync(fixture.Action with
        {
            Permissions = fixture.Action.Permissions
                .Append(new ActionPermissionRequirementDefinition(effectAction, effectEntity))
                .ToArray()
        });

        await AssertInvalidDefinitionAsync();
    }

    [PostgresFact]
    public async Task Persisted_role_with_wildcard_effect_grant_is_rejected_as_one_invalid_document()
    {
        if (!fixture.Available)
        {
            return;
        }

        var original = await fixture.ReadRoleGrantsJsonAsync("effect_operator");
        var before = await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "customers",
            fixture.CustomerId);
        try
        {
            await fixture.PersistRoleGrantsJsonAsync(
                "effect_operator",
                """
                [
                  {"scope":"entity","action":"read","entity":"customer","target":""},
                  {"scope":"entity","action":"effect_update","entity":"*","target":""},
                  {"scope":"action","action":"execute","entity":"customer","target":"effect_update_customer"},
                  {"scope":"field","action":"effect_write","entity":"customer","target":"name"}
                ]
                """);

            var visible = await fixture.Actions.ListRecordActionsAsync(
                fixture.Tenant,
                fixture.EffectOperator,
                "customers",
                fixture.CustomerId);
            visible.Should().NotContain(action => action.Key == fixture.EffectAction.Key);

            var execute = () => fixture.Actions.ExecuteActionAsync(
                fixture.Tenant,
                fixture.EffectOperator,
                "customers",
                fixture.CustomerId,
                fixture.EffectAction.Key);
            var denied = await execute.Should().ThrowAsync<RuntimeActionPermissionException>();
            denied.Which.PermissionAction.Should().Be("named_action.execute");
            denied.Which.Target.Should().Be(fixture.EffectAction.Key);
            (await fixture.Records.GetRecordAsync(
                fixture.Tenant,
                "customers",
                fixture.CustomerId))!["name"].Should().Be(before!["name"]);
        }
        finally
        {
            await fixture.PersistRoleGrantsJsonAsync("effect_operator", original);
        }
    }

    [PostgresFact]
    public async Task Action_listing_completes_with_a_single_connection_pool()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.PersistActionAsync(fixture.Action);
        var actions = fixture.CreateActionsWithSingleConnectionPool();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var visible = await actions.ListRecordActionsAsync(
            fixture.Tenant,
            fixture.Billing,
            "customers",
            fixture.CustomerId,
            timeout.Token);

        visible.Should().Contain(action => action.Key == fixture.Action.Key);
    }

    private async Task AssertInvalidDefinitionAsync()
    {
        var visible = await fixture.Actions.ListRecordActionsAsync(
            fixture.Tenant,
            fixture.Owner,
            "customers",
            fixture.CustomerId);
        visible.Should().NotContain(action => action.Key == fixture.Action.Key);

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Owner,
            "customers",
            fixture.CustomerId,
            fixture.Action.Key);
        await execute.Should().ThrowAsync<RuntimeActionDefinitionException>();
    }
}

public sealed class RuntimeActionPermissionEffectDatabaseFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? adminConnectionString;
    private string? databaseName;

    public bool Available { get; private set; }

    public ResolvedTenantEnvironment Tenant { get; private set; } = null!;

    public TenantDatabaseConnectionFactory Factory { get; private set; } = null!;

    public RuntimeRecordStore Records { get; private set; } = null!;

    public RuntimeActionStore Actions { get; private set; } = null!;

    public RuntimeUserContext Owner { get; } = new(
        7001,
        "owner@action-effect.test",
        RuntimeSystemRoleIds.TenantOwner)
    {
        Roles = [RuntimeSystemRoleIds.TenantOwner]
    };

    public RuntimeUserContext Billing { get; } = new(7002, "billing@action-effect.test", "billing") { Roles = ["billing"] };

    public RuntimeUserContext EffectOperator { get; } = new(
        7003,
        "effect@action-effect.test",
        "effect_operator")
    {
        Roles = ["effect_operator"]
    };

    public IReadOnlyDictionary<string, RuntimeUserContext> EffectDenialUsers { get; } =
        new Dictionary<string, RuntimeUserContext>(StringComparer.Ordinal)
        {
            ["missing_effect"] = new(7004, "missing-effect@action-effect.test", "missing_effect")
            {
                Roles = ["missing_effect"]
            },
            ["missing_action"] = new(7005, "missing-action@action-effect.test", "missing_action")
            {
                Roles = ["missing_action"]
            },
            ["missing_effect_write"] = new(7006, "missing-field@action-effect.test", "missing_effect_write")
            {
                Roles = ["missing_effect_write"]
            }
        };

    public ActionDefinition Action { get; private set; } = null!;

    public ActionDefinition EffectAction { get; private set; } = null!;

    public ActionDefinition EffectCreateAction { get; private set; } = null!;

    public long CustomerId { get; private set; }

    public async Task InitializeAsync()
    {
        // Keep fixture initialization defensive; every consuming test has a PostgreSQL-aware attribute,
        // so a missing test database is still reported as an explicit skip instead of a passing return.
        if (!TestPostgres.TryGetAdminConnectionString(out var connectionString))
        {
            return;
        }

        adminConnectionString = connectionString;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        databaseName = $"erp_tenant_action_effect_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        Tenant = RuntimeTestTenant.Create("action-effect", "action-effect.erpgenerator.example", databaseName);
        Factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
        var runtimeDatabase = new RuntimeDatabase(Factory, new RuntimeSqlBuilder());
        Records = new RuntimeRecordStore(Factory);
        Actions = new RuntimeActionStore(Factory, new RuntimePermissionService(Factory));
        CompiledBundle compiled;
        try
        {
            compiled = new ErpBundleCompiler().Compile(CreateBundle());
        }
        catch (BundleCompilationException exception)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, exception.Errors), exception);
        }
        await runtimeDatabase.ApplyCompiledBundleAsync(Tenant, compiled);
        Action = compiled.Actions.Single(action => action.Key == "create_invoice");
        EffectAction = compiled.Actions.Single(action => action.Key == "effect_update_customer");
        EffectCreateAction = compiled.Actions.Single(action => action.Key == "effect_create_invoice");

        var customer = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "customers",
            RuntimeJson.JsonObject(("name", $"Action Effect {suffix}")));
        CustomerId = Convert.ToInt64(customer["id"]);
        Available = true;
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(adminConnectionString) && !string.IsNullOrWhiteSpace(databaseName))
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    public async Task PersistActionAsync(ActionDefinition action) =>
        await PersistMetadataJsonAsync(JsonSerializer.Serialize(action, JsonOptions));

    public async Task PersistMetadataJsonAsync(string metadataJson)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.action_definitions
            set metadata = cast(@metadata as jsonb)
            where entity_id = @entityId and id = @actionId
            """;
        command.Parameters.AddWithValue("metadata", metadataJson);
        command.Parameters.AddWithValue("entityId", "customer");
        command.Parameters.AddWithValue("actionId", Action.Key);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    public async Task<string> ReadRoleGrantsJsonAsync(string role)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select grants::text from _runtime.role_definitions where id = @role";
        command.Parameters.AddWithValue("role", role);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public async Task PersistRoleGrantsJsonAsync(string role, string grantsJson)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update _runtime.role_definitions set grants = cast(@grants as jsonb) where id = @role";
        command.Parameters.AddWithValue("grants", grantsJson);
        command.Parameters.AddWithValue("role", role);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    public async Task<long> InvoiceCountAsync() =>
        (await Records.ListRecordsAsync(Tenant, "invoices", new RuntimeRecordQuery(PageSize: 100))).Total;

    public RuntimeActionStore CreateActionsWithSingleConnectionPool()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize(adminConnectionString!))
        {
            MaxPoolSize = 1,
            Timeout = 2
        };
        var factory = TestPostgres.CreateConnectionFactory(builder.ConnectionString);
        return new RuntimeActionStore(factory, new RuntimePermissionService(factory));
    }

    private static ErpBundle CreateBundle() => new()
    {
        Id = "runtime_action_permission_effect",
        Name = "Runtime Action Permission Effect",
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
                        Fields = [new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]
                    },
                    new ErpEntity
                    {
                        Id = "invoice",
                        Name = "invoices",
                        DisplayName = "Invoices",
                        Fields =
                        [
                            new ErpField { Id = "customer", Name = "customer_id", DisplayName = "Customer", Type = "relation", Required = true, References = "customer" },
                            new ErpField { Id = "amount", Name = "amount", DisplayName = "Amount", Type = "money", Required = true }
                        ]
                    }
                ]
            }
        ],
        Actions =
        [
            new ErpAction
            {
                Key = "create_invoice",
                Label = "Create invoice",
                Entity = "customer",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions = [new ErpGrant { Action = "create", Entity = "invoice" }]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "create_invoice",
                        Type = "create_record",
                        Entity = "invoice",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                            ["amount"] = new() { Value = 10 }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "effect_update_customer",
                Label = "Customer name transition",
                Entity = "customer",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Action = "read", Entity = "customer" },
                        new ErpGrant { Action = "effect_update", Entity = "customer" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "effect_update_customer",
                        Type = "update_current_record",
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["name"] = new() { Value = "Effect action updated" }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "effect_create_invoice",
                Label = "Invoice issuance",
                Entity = "customer",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Action = "read", Entity = "customer" },
                        new ErpGrant { Action = "effect_create", Entity = "invoice" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = "effect_create_invoice",
                        Type = "create_record",
                        Entity = "invoice",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                            ["amount"] = new() { Value = 22m }
                        }
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["billing"] = new()
            {
                DisplayName = "Billing",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "customer" },
                    new ErpGrant { Action = "create", Entity = "invoice" },
                    new ErpGrant
                    {
                        Scope = PermissionGrantScopes.Action,
                        Action = GranularPermissionActions.Execute,
                        Entity = "customer",
                        Target = "create_invoice"
                    }
                ]
            },
            ["effect_operator"] = FullEffectRole(),
            ["missing_effect"] = EffectRole(includeEffect: false, includeAction: true, includeEffectWrite: true),
            ["missing_action"] = EffectRole(includeEffect: true, includeAction: false, includeEffectWrite: true),
            ["missing_effect_write"] = EffectRole(includeEffect: true, includeAction: true, includeEffectWrite: false)
        }
    };

    private static ErpRole EffectRole(
        bool includeEffect,
        bool includeAction,
        bool includeEffectWrite)
    {
        var grants = new List<ErpGrant>
        {
            new() { Action = "read", Entity = "customer" }
        };
        if (includeEffect)
        {
            grants.Add(new ErpGrant { Action = "effect_update", Entity = "customer" });
        }

        if (includeEffectWrite)
        {
            grants.Add(new ErpGrant
            {
                Scope = PermissionGrantScopes.Field,
                Action = GranularPermissionActions.EffectWrite,
                Entity = "customer",
                Target = "name"
            });
        }

        if (includeAction)
        {
            grants.Add(new ErpGrant
            {
                Scope = PermissionGrantScopes.Action,
                Action = GranularPermissionActions.Execute,
                Entity = "customer",
                Target = "effect_update_customer"
            });
        }

        return new ErpRole { DisplayName = "Effect role", Grants = grants };
    }

    private static ErpRole FullEffectRole()
    {
        var role = EffectRole(includeEffect: true, includeAction: true, includeEffectWrite: true);
        role.Grants.AddRange(
        [
            new ErpGrant { Action = "effect_create", Entity = "invoice" },
            new ErpGrant
            {
                Scope = PermissionGrantScopes.Field,
                Action = GranularPermissionActions.EffectWrite,
                Entity = "invoice",
                Target = "customer"
            },
            new ErpGrant
            {
                Scope = PermissionGrantScopes.Field,
                Action = GranularPermissionActions.EffectWrite,
                Entity = "invoice",
                Target = "amount"
            },
            new ErpGrant
            {
                Scope = PermissionGrantScopes.Action,
                Action = GranularPermissionActions.Execute,
                Entity = "customer",
                Target = "effect_create_invoice"
            }
        ]);
        return role;
    }
}
