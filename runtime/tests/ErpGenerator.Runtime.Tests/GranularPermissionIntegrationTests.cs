using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class GranularPermissionIntegrationTests
{
    [Fact]
    public void Compiler_preserves_typed_grants_and_rejects_unenforceable_or_dangling_contracts()
    {
        var compiled = new ErpBundleCompiler().Compile(Bundle());
        var role = compiled.Roles.Single(candidate => candidate.Id == "restricted");
        role.Grants.Should().Contain(grant =>
            grant.Scope == PermissionGrantScopes.Field &&
            grant.Entity == "order" &&
            grant.Target == "name" &&
            grant.Action == "read");
        role.Grants.Should().Contain(grant =>
            grant.Scope == PermissionGrantScopes.Page &&
            grant.Entity == "order" &&
            grant.Target == "entity_list" &&
            grant.Action == "view");
        role.Grants.Should().Contain(grant =>
            grant.Scope == PermissionGrantScopes.Action &&
            grant.Entity == "order" &&
            grant.Target == "close_order" &&
            grant.Action == "execute");

        foreach (var invalid in new[]
                 {
                     new ErpGrant { Scope = PermissionGrantScopes.Field, Action = "read", Entity = "order", Target = "missing" },
                     new ErpGrant { Scope = PermissionGrantScopes.Page, Action = "view", Entity = "*", Target = "admin_console" },
                     new ErpGrant { Scope = PermissionGrantScopes.Action, Action = "execute", Entity = "order", Target = "missing_action" },
                     new ErpGrant { Scope = "record", Action = "read", Entity = "order", Target = "created_by=current_user" }
                 })
        {
            var bundle = Bundle();
            bundle.Roles["restricted"].Grants.Add(invalid);

            var compile = () => new ErpBundleCompiler().Compile(bundle);

            compile.Should().Throw<BundleCompilationException>()
                .Which.Errors.Should().NotBeEmpty();
        }
    }

    [PostgresFact]
    public async Task Runtime_enforces_field_page_and_named_action_grants_fail_closed_in_real_routes()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_permission_scope_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var actions = new RuntimeActionStore(factory, permissions);
            var workflows = new RuntimeWorkflowStore(factory, permissions);
            var users = new RuntimeUserStore(factory);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, new ErpBundleCompiler().Compile(Bundle()));

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"owner-{suffix}@example.test");
            var created = await records.CreateRecordAsync(
                tenant,
                owner,
                "order",
                RuntimeJson.JsonObject(("name", $"Order {suffix}"), ("status", "draft"), ("secret", $"SECRET-{suffix}")));
            var member = await InviteAndActivateAsync(
                users,
                runtimeDatabase,
                tenant,
                owner,
                $"restricted-{suffix}@example.test",
                "restricted");
            var legacy = await InviteAndActivateAsync(
                users,
                runtimeDatabase,
                tenant,
                owner,
                $"legacy-{suffix}@example.test",
                "legacy");
            var entity = (await records.GetEntityAsync(tenant, "order"))!;

            (await permissions.IsFieldAllowedAsync(tenant, member, "read", entity, "name")).Should().BeTrue();
            (await permissions.IsFieldAllowedAsync(tenant, member, "read", entity, "secret")).Should().BeFalse();
            (await permissions.IsPageAllowedAsync(tenant, member, "entity_list", "order")).Should().BeTrue();
            (await permissions.IsPageAllowedAsync(tenant, member, "dashboard", "*")).Should().BeFalse();
            (await permissions.IsNamedActionAllowedAsync(tenant, member, entity, "close_order")).Should().BeTrue();
            (await permissions.IsNamedActionAllowedAsync(tenant, member, entity, "reopen_order")).Should().BeFalse();
            (await permissions.IsFieldAllowedAsync(tenant, legacy, "read", entity, "secret")).Should().BeTrue(
                "an empty field-grant list preserves the legacy entity permission contract");
            (await permissions.IsPageAllowedAsync(tenant, legacy, "dashboard", "*")).Should().BeTrue(
                "an empty page-grant list preserves legacy page visibility");
            (await permissions.IsNamedActionAllowedAsync(tenant, legacy, entity, "reopen_order")).Should().BeFalse(
                "non-owner users require one exact named-action grant");
            (await permissions.IsFieldAllowedAsync(tenant, owner, "read", entity, "secret")).Should().BeTrue();
            (await permissions.IsPageAllowedAsync(tenant, owner, "dashboard", "*")).Should().BeTrue();
            (await permissions.IsNamedActionAllowedAsync(tenant, owner, entity, "reopen_order")).Should().BeTrue();

            var conditionStart = await workflows.StartActionAsync(
                tenant,
                member,
                "order",
                Convert.ToInt64(created["id"]),
                "evaluate_secret");
            var conditionHistory = await workflows.GetInstanceAsync(
                tenant,
                member,
                conditionStart.Instance.Id);
            var conditionEvent = conditionHistory!.Events.Should().ContainSingle(@event =>
                @event.EventType == "workflow.condition.evaluated").Subject;
            conditionEvent.Metadata.GetProperty("result").GetBoolean().Should().BeTrue();
            conditionEvent.Metadata.GetProperty("operator").GetString().Should().Be("exists");
            conditionEvent.Metadata.TryGetProperty("left", out _).Should().BeFalse();
            conditionEvent.Metadata.TryGetProperty("right", out _).Should().BeFalse();
            conditionEvent.Metadata.GetRawText().Should().NotContain($"SECRET-{suffix}",
                "workflow history must not expose operands from an unreadable condition field");

            var visibleActions = await actions.ListRecordActionsAsync(
                tenant,
                member,
                "order",
                Convert.ToInt64(created["id"]));
            visibleActions.Select(action => action.Key).Should().Equal("close_order");

            var sessionSecret = $"granular-permission-{suffix}";
            using var application = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Runtime:SessionSecret"] = sessionSecret,
                            ["RuntimeDatabase:TenantConnectionStringTemplate"] = adminConnectionString
                        });
                    });
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IControlPlaneClient>();
                        services.AddSingleton<IControlPlaneClient>(new MultiTenantControlPlaneClient(tenant));
                    });
                });
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var cookie = RuntimeHttpTestHelper.CreateSessionCookie(
                sessionSecret,
                tenant.TenantStableId,
                tenant.Host,
                member.UserId,
                member.Email,
                member.Role);

            using var listRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get,
                "/api/runtime/entities/order/records",
                tenant.Host,
                cookie);
            var listResponse = await client.SendAsync(listRequest);
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()))
            {
                var record = listJson.RootElement.GetProperty("records")[0];
                record.TryGetProperty("name", out _).Should().BeTrue();
                record.TryGetProperty("status", out _).Should().BeTrue();
                record.TryGetProperty("secret", out _).Should().BeFalse();
            }

            using var deniedWrite = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Patch,
                $"/api/runtime/entities/order/records/{created["id"]}",
                tenant.Host,
                cookie);
            deniedWrite.Content = JsonContent.Create(new { secret = "should-not-write" });
            (await client.SendAsync(deniedWrite)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var listPage = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get,
                "/entities/order",
                tenant.Host,
                cookie);
            var listPageResponse = await client.SendAsync(listPage);
            listPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await listPageResponse.Content.ReadAsStringAsync()).Should().NotContain($"SECRET-{suffix}");

            using var dashboard = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/", tenant.Host, cookie);
            (await client.SendAsync(dashboard)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var close = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Post,
                $"/api/runtime/entities/order/records/{created["id"]}/actions/close_order",
                tenant.Host,
                cookie);
            (await client.SendAsync(close)).StatusCode.Should().Be(HttpStatusCode.OK);

            using var reopen = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Post,
                $"/api/runtime/entities/order/records/{created["id"]}/actions/reopen_order",
                tenant.Host,
                cookie);
            (await client.SendAsync(reopen)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var persisted = await records.GetRecordAsync(tenant, "order", Convert.ToInt64(created["id"]));
            persisted!["status"].Should().Be("closed");
            persisted["secret"].Should().Be($"SECRET-{suffix}");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Effectful_record_and_action_stores_enforce_field_allowlists_atomically()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_field_boundary_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create("field-boundary", "field-boundary.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var actions = new RuntimeActionStore(factory, permissions);
            var compiled = new ErpBundleCompiler().Compile(Bundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = new RuntimeUserContext(
                -80_001,
                "owner@field-boundary.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var restricted = new RuntimeUserContext(-80_002, "restricted@field-boundary.test", "restricted")
            {
                Roles = ["restricted"]
            };
            var created = await records.CreateRecordAsync(
                tenant,
                owner,
                "order",
                RuntimeJson.JsonObject(
                    ("name", $"Boundary {suffix}"),
                    ("status", "draft"),
                    ("secret", $"SECRET-{suffix}")));
            var recordId = Convert.ToInt64(created["id"]);
            var rowCountBefore = (await records.ListRecordsAsync(
                tenant,
                "order",
                new RuntimeRecordQuery(PageSize: 100))).Total;
            var auditCountBefore = await CountAuditEventsAsync(factory, tenant);

            var deniedCreate = () => records.CreateRecordAsync(
                tenant,
                restricted,
                "order",
                RuntimeJson.JsonObject(
                    ("name", "Forbidden create"),
                    ("status", "draft"),
                    ("secret", "must not persist")));
            var createException = await deniedCreate.Should().ThrowAsync<RuntimeRecordPermissionException>();
            createException.Which.Action.Should().Be("field.write");

            var deniedUpdate = () => records.UpdateRecordAsync(
                tenant,
                restricted,
                "order",
                recordId,
                RuntimeJson.JsonObject(("secret", "must not persist")));
            var updateException = await deniedUpdate.Should().ThrowAsync<RuntimeRecordPermissionException>();
            updateException.Which.Action.Should().Be("field.write");

            (await records.ListRecordsAsync(tenant, "order", new RuntimeRecordQuery(PageSize: 100))).Total
                .Should().Be(rowCountBefore);
            (await CountAuditEventsAsync(factory, tenant)).Should().Be(auditCountBefore);
            (await records.GetRecordAsync(tenant, "order", recordId))!["secret"]
                .Should().Be($"SECRET-{suffix}");

            var originalAction = compiled.Actions.Single(action => action.Key == "close_order");
            var originalStep = originalAction.Steps.Single();
            (await actions.ListRecordActionsAsync(tenant, restricted, "order", recordId))
                .Should().ContainSingle(action => action.Key == originalAction.Key);
            var setRestrictedField = originalAction with
            {
                Steps =
                [
                    originalStep with
                    {
                        Set = new Dictionary<string, ActionValueDefinition>
                        {
                            ["secret"] = new("mutated", null, null, null)
                        }
                    }
                ]
            };
            var filterRestrictedField = originalAction with
            {
                Permissions =
                [
                    new ActionPermissionRequirementDefinition("read", "order"),
                    new ActionPermissionRequirementDefinition("update", "order")
                ],
                Steps =
                [
                    originalStep with
                    {
                        Id = "find_by_secret",
                        Type = "query_records",
                        Entity = "order",
                        Where = [new ActionFilterDefinition("secret", "eq", $"SECRET-{suffix}", null, [])],
                        Set = new Dictionary<string, ActionValueDefinition>()
                    },
                    originalStep with { Id = "update_after_secret_filter" }
                ]
            };
            var copyRestrictedSource = originalAction with
            {
                Steps =
                [
                    originalStep with
                    {
                        Set = new Dictionary<string, ActionValueDefinition>
                        {
                            ["status"] = new(
                                null,
                                new ActionValueFromDefinition("secret", null, null, null, null),
                                null,
                                null)
                        }
                    }
                ]
            };
            var guardRestrictedField = originalAction with
            {
                Steps =
                [
                    originalStep with
                    {
                        Id = "guard_secret",
                        Type = "guard",
                        Condition = new ActionConditionDefinition(
                            "exists",
                            new ActionOperandDefinition("secret", null, null, null),
                            null,
                            []),
                        Error = "Secret is required.",
                        Set = new Dictionary<string, ActionValueDefinition>()
                    },
                    originalStep with { Id = "update_after_secret_guard" }
                ]
            };
            var templateRestrictedSource = originalAction with
            {
                Steps =
                [
                    originalStep with
                    {
                        Set = new Dictionary<string, ActionValueDefinition>
                        {
                            ["name"] = new(null, null, "{{ record.secret }}", null)
                        }
                    }
                ]
            };
            var aggregateRestrictedItem = originalAction with
            {
                Permissions =
                [
                    new ActionPermissionRequirementDefinition("read", "order"),
                    new ActionPermissionRequirementDefinition("update", "order")
                ],
                Steps =
                [
                    originalStep with
                    {
                        Id = "find_draft_orders",
                        Type = "query_records",
                        Entity = "order",
                        Where = [new ActionFilterDefinition("status", "eq", "draft", null, [])],
                        Set = new Dictionary<string, ActionValueDefinition>()
                    },
                    originalStep with
                    {
                        Id = "update_from_secret_aggregate",
                        Set = new Dictionary<string, ActionValueDefinition>
                        {
                            ["name"] = new(
                                null,
                                null,
                                null,
                                new ActionAggregateDefinition(
                                    "find_draft_orders",
                                    "sum",
                                    new ActionExpressionDefinition(
                                        "add",
                                        new ActionOperandDefinition(
                                            null,
                                            null,
                                            null,
                                            new ActionValueFromDefinition(null, "secret", null, null, null)),
                                        new ActionOperandDefinition(null, 0, null, null))))
                        }
                    }
                ]
            };
            var auditRestrictedMetadata = originalAction with
            {
                Steps =
                [
                    originalStep with
                    {
                        Id = "audit_secret",
                        Type = "audit_event",
                        Event = "order.secret.observed",
                        Metadata = new Dictionary<string, ActionValueDefinition>
                        {
                            ["secret"] = new(
                                null,
                                new ActionValueFromDefinition("secret", null, null, null, null),
                                null,
                                null)
                        },
                        Set = new Dictionary<string, ActionValueDefinition>()
                    },
                    originalStep with { Id = "update_after_secret_audit" }
                ]
            };
            var filterRestrictedSource = originalAction with
            {
                Permissions =
                [
                    new ActionPermissionRequirementDefinition("read", "order"),
                    new ActionPermissionRequirementDefinition("update", "order")
                ],
                Steps =
                [
                    originalStep with
                    {
                        Id = "find_from_secret",
                        Type = "query_records",
                        Entity = "order",
                        Where =
                        [
                            new ActionFilterDefinition(
                                "status",
                                "eq",
                                null,
                                new ActionValueFromDefinition("secret", null, null, null, null),
                                [])
                        ],
                        Set = new Dictionary<string, ActionValueDefinition>()
                    },
                    originalStep with { Id = "update_after_secret_source_filter" }
                ]
            };

            foreach (var (definition, expectedPermission) in new[]
                     {
                         (setRestrictedField, "field.write"),
                         (filterRestrictedField, "field.read"),
                         (copyRestrictedSource, "field.read"),
                         (guardRestrictedField, "field.read"),
                         (templateRestrictedSource, "field.read"),
                         (aggregateRestrictedItem, "field.read"),
                         (auditRestrictedMetadata, "field.read"),
                         (filterRestrictedSource, "field.read")
                     })
            {
                await PersistActionAsync(factory, tenant, definition);
                (await actions.ListRecordActionsAsync(tenant, restricted, "order", recordId))
                    .Should().NotContain(action => action.Key == definition.Key,
                        "action discovery must enforce every executable step field dependency");
                var execute = () => actions.ExecuteActionAsync(
                    tenant,
                    restricted,
                    "order",
                    recordId,
                    definition.Key);
                var exception = await execute.Should().ThrowAsync<RuntimeActionPermissionException>();
                exception.Which.PermissionAction.Should().Be(expectedPermission);
                (await CountAuditEventsAsync(factory, tenant)).Should().Be(auditCountBefore,
                    "field-denied action effects and their transaction-local audit trail must roll back atomically");
                var unchanged = await records.GetRecordAsync(tenant, "order", recordId);
                unchanged!["status"].Should().Be("draft");
                unchanged["secret"].Should().Be($"SECRET-{suffix}");
            }

            var copySystemCreatedBy = originalAction with
            {
                Steps =
                [
                    originalStep with
                    {
                        Set = new Dictionary<string, ActionValueDefinition>
                        {
                            ["name"] = new(
                                null,
                                new ActionValueFromDefinition("created_by", null, null, null, null),
                                null,
                                null)
                        }
                    }
                ]
            };
            await PersistActionAsync(factory, tenant, copySystemCreatedBy);
            (await actions.ListRecordActionsAsync(tenant, restricted, "order", recordId))
                .Should().ContainSingle(action => action.Key == copySystemCreatedBy.Key,
                    "the actual created_by system field remains readable");
            await PersistFieldNameAsync(factory, tenant, "order.secret", "created_by");
            try
            {
                (await actions.ListRecordActionsAsync(tenant, restricted, "order", recordId))
                    .Should().NotContain(action => action.Key == copySystemCreatedBy.Key,
                        "a declared field collision must not inherit the system-field read exemption");
                var executeCollision = () => actions.ExecuteActionAsync(
                    tenant,
                    restricted,
                    "order",
                    recordId,
                    copySystemCreatedBy.Key);
                (await executeCollision.Should().ThrowAsync<RuntimeActionPermissionException>())
                    .Which.PermissionAction.Should().Be("field.read");
                (await records.GetRecordAsync(tenant, "order", recordId))!["name"]
                    .Should().Be($"Boundary {suffix}",
                        "the colliding business field must never be copied through a system-field reference");
            }
            finally
            {
                await PersistFieldNameAsync(factory, tenant, "order.secret", "secret");
            }

            await PersistActionAsync(factory, tenant, originalAction);
            var entity = (await records.GetEntityAsync(tenant, "order"))!;
            (await permissions.IsFieldAllowedAsync(tenant, restricted, "write", entity, "status"))
                .Should().BeTrue("this models a successful route-level precheck before a late revocation");
            var restrictedRole = compiled.Roles.Single(role => role.Id == "restricted");
            var revokedGrants = restrictedRole.Grants
                .Where(grant =>
                    !(grant.Scope.Equals(PermissionGrantScopes.Field, StringComparison.OrdinalIgnoreCase) &&
                      grant.Action.Equals("write", StringComparison.OrdinalIgnoreCase) &&
                      grant.Target.Equals("status", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            await PersistRoleGrantsAsync(factory, tenant, "restricted", revokedGrants);
            try
            {
                (await actions.ListRecordActionsAsync(tenant, restricted, "order", recordId))
                    .Should().NotContain(action => action.Key == originalAction.Key,
                        "an action must disappear as soon as a required field grant is revoked");
                var lateRecordUpdate = () => records.UpdateRecordAsync(
                    tenant,
                    restricted,
                    "order",
                    recordId,
                    RuntimeJson.JsonObject(("status", "closed")));
                (await lateRecordUpdate.Should().ThrowAsync<RuntimeRecordPermissionException>())
                    .Which.Action.Should().Be("field.write");

                var lateAction = () => actions.ExecuteActionAsync(
                    tenant,
                    restricted,
                    "order",
                    recordId,
                    originalAction.Key);
                (await lateAction.Should().ThrowAsync<RuntimeActionPermissionException>())
                    .Which.PermissionAction.Should().Be("field.write");
            }
            finally
            {
                await PersistRoleGrantsAsync(factory, tenant, "restricted", restrictedRole.Grants);
            }

            (await CountAuditEventsAsync(factory, tenant)).Should().Be(auditCountBefore);
            var afterRevocation = await records.GetRecordAsync(tenant, "order", recordId);
            afterRevocation!["status"].Should().Be("draft");
            afterRevocation["secret"].Should().Be($"SECRET-{suffix}");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static async Task PersistActionAsync(
        ITenantDatabaseConnectionFactory factory,
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        ActionDefinition action)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.action_definitions
            set metadata = cast(@metadata as jsonb)
            where id = @actionId and entity_id = @entityId
            """;
        command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(action));
        command.Parameters.AddWithValue("actionId", action.Key);
        command.Parameters.AddWithValue("entityId", action.Entity);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task PersistRoleGrantsAsync(
        ITenantDatabaseConnectionFactory factory,
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        string roleId,
        IReadOnlyList<PermissionGrantDefinition> grants)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.role_definitions
            set grants = cast(@grants as jsonb), updated_at = now()
            where id = @roleId
            """;
        command.Parameters.AddWithValue("grants", JsonSerializer.Serialize(grants));
        command.Parameters.AddWithValue("roleId", roleId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task PersistFieldNameAsync(
        ITenantDatabaseConnectionFactory factory,
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        string fieldId,
        string name)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update _runtime.field_definitions set name = @name where id = @fieldId";
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("fieldId", fieldId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task<long> CountAuditEventsAsync(
        ITenantDatabaseConnectionFactory factory,
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from audit.events";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<RuntimeUserContext> InviteAndActivateAsync(
        RuntimeUserStore users,
        RuntimeDatabase runtimeDatabase,
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        RuntimeUserContext owner,
        string email,
        string role)
    {
        var invite = await users.InviteUserAsync(
            tenant,
            owner,
            email,
            $"{role} operator",
            [role],
            null,
            TimeSpan.FromDays(1));
        await users.ActivateInvitedUserAsync(
            tenant,
            invite.ActivationToken!,
            email,
            $"{role} operator",
            "MemberPass123!");
        var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, "MemberPass123!");
        return new RuntimeUserContext(login!.Id, login.Email, login.Role) { Roles = login.Roles };
    }

    private static ErpBundle Bundle() => new()
    {
        Id = "granular_permissions",
        Name = "Granular permissions",
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
                        Id = "order",
                        Name = "orders",
                        DisplayName = "Order",
                        Display = new ErpEntityDisplay { Primary = "name", Secondary = "status" },
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                            new ErpField { Id = "status", Name = "status", DisplayName = "Status", Type = "enum", Required = true, Options = ["draft", "closed"] },
                            new ErpField { Id = "secret", Name = "secret", DisplayName = "Secret", Type = "string" }
                        ]
                    }
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = "secret_evaluation",
                        Name = "Secret evaluation",
                        Entity = "order",
                        Trigger = new ErpWorkflowTrigger { Action = "evaluate_secret" },
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "secret_exists",
                                Type = "condition",
                                Condition = new ErpWorkflowCondition
                                {
                                    Op = "exists",
                                    Left = new ErpWorkflowConditionOperand { Field = "secret" }
                                },
                                Then =
                                [
                                    new ErpWorkflowStep
                                    {
                                        Id = "record_secret_match",
                                        Type = "audit_event",
                                        Event = "order.secret.condition_matched",
                                        Message = "Secret condition matched."
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        ],
        Actions = [Action("close_order", "closed"), Action("reopen_order", "draft")],
        Roles = new Dictionary<string, ErpRole>
        {
            ["restricted"] = new()
            {
                DisplayName = "Restricted",
                Grants =
                [
                    new ErpGrant { Entity = "order", Action = "read" },
                    new ErpGrant { Entity = "order", Action = "create" },
                    new ErpGrant { Entity = "order", Action = "update" },
                    new ErpGrant { Entity = "order", Action = "start_workflow" },
                    new ErpGrant { Entity = "order", Action = "read_task" },
                    Field("read", "name"),
                    Field("read", "status"),
                    Field("write", "name"),
                    Field("write", "status"),
                    new ErpGrant { Scope = PermissionGrantScopes.Page, Action = "view", Entity = "order", Target = "entity_list" },
                    new ErpGrant { Scope = PermissionGrantScopes.Action, Action = "execute", Entity = "order", Target = "close_order" }
                ]
            },
            ["legacy"] = new()
            {
                DisplayName = "Legacy",
                Grants =
                [
                    new ErpGrant { Entity = "order", Action = "read" },
                    new ErpGrant { Entity = "order", Action = "create" },
                    new ErpGrant { Entity = "order", Action = "update" }
                ]
            }
        }
    };

    private static ErpGrant Field(string action, string target) => new()
    {
        Scope = PermissionGrantScopes.Field,
        Action = action,
        Entity = "order",
        Target = target
    };

    private static ErpAction Action(string key, string status) => new()
    {
        Key = key,
        Label = key.Replace('_', ' '),
        Entity = "order",
        Type = "record_action",
        Requires = new ErpActionRequires
        {
            Permissions = [new ErpGrant { Entity = "order", Action = "update" }]
        },
        Steps =
        [
            new ErpActionStep
            {
                Id = $"{key}_update",
                Type = "update_current_record",
                Entity = "order",
                Set = new Dictionary<string, ErpActionValue>
                {
                    ["status"] = new() { Value = status }
                }
            }
        ]
    };
}
