using System.Net;
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

public sealed class RuntimeFieldReadIsolationIntegrationTests
{
    [PostgresFact]
    public async Task Hidden_fields_cannot_influence_queries_or_leak_through_relation_labels()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_field_read_isolation_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var users = new RuntimeUserStore(factory);
            var compiled = WithAuthoritativeLabelCollision(new ErpBundleCompiler().Compile(Bundle()));
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"owner-{suffix}@example.test");
            var customerName = $"Classified Customer {suffix}";
            var customerEmail = $"classified-{suffix}@example.test";
            var hiddenNeedle = $"HIDDEN-NEEDLE-{suffix}";
            var customer = await records.CreateRecordAsync(
                tenant,
                owner,
                "customer",
                RuntimeJson.JsonObject(
                    ("name", customerName),
                    ("email", customerEmail),
                    ("public_code", $"C-{suffix}")));
            var order = await records.CreateRecordAsync(
                tenant,
                owner,
                "order",
                RuntimeJson.JsonObject(
                    ("name", $"Visible order {suffix}"),
                    ("status", "draft"),
                    ("secret", hiddenNeedle),
                    ("customer_id", customer["id"]!)));
            await records.CreateRecordAsync(
                tenant,
                owner,
                "order",
                RuntimeJson.JsonObject(
                    ("name", $"Another visible order {suffix}"),
                    ("status", "draft"),
                    ("secret", $"OTHER-{suffix}"),
                    ("customer_id", customer["id"]!)));

            var member = await InviteAndActivateAsync(
                users,
                runtimeDatabase,
                tenant,
                owner,
                $"restricted-{suffix}@example.test",
                "restricted");
            var primaryReader = await InviteAndActivateAsync(
                users,
                runtimeDatabase,
                tenant,
                owner,
                $"primary-reader-{suffix}@example.test",
                "primary_reader");

            var secretSearch = await records.ListRecordsAsync(
                tenant,
                member,
                "order",
                new RuntimeRecordQuery(PageSize: 20, Search: hiddenNeedle));
            secretSearch.Total.Should().Be(0);
            secretSearch.Records.Should().BeEmpty();

            var unfilteredList = await records.ListRecordsAsync(
                tenant,
                member,
                "order",
                new RuntimeRecordQuery(PageSize: 20));
            unfilteredList.Entity.Fields.Should().NotContain(field => field.Name == "secret");
            unfilteredList.Records.Should().OnlyContain(record => !record.ContainsKey("secret"));

            var hiddenSort = async () => await records.ListRecordsAsync(
                tenant,
                member,
                "order",
                new RuntimeRecordQuery(PageSize: 20, SortBy: "secret"));
            (await hiddenSort.Should().ThrowAsync<RuntimeRecordValidationException>())
                .Which.Errors.Should().ContainSingle(error => error.Field == "sortBy");

            (await records.LookupRecordsAsync(tenant, member, "order", hiddenNeedle))
                .Should().BeEmpty();

            var hydrated = await records.GetRecordAsync(
                tenant,
                member,
                "order",
                Convert.ToInt64(order["id"]));
            hydrated.Should().NotBeNull();
            hydrated.Should().NotContainKey("secret");
            hydrated!["customer_id__label"].Should().Be($"#{customer["id"]}");
            hydrated["customer_id__secondaryLabel"].Should().BeNull();

            var allowedUpdate = await records.UpdateRecordAsync(
                tenant,
                member,
                "order",
                Convert.ToInt64(order["id"]),
                RuntimeJson.JsonObject(("status", "closed")));
            allowedUpdate.Should().NotBeNull();
            allowedUpdate.Should().NotContainKey("secret");

            var orderEntity = (await records.GetEntityAsync(tenant, "order"))!;
            var visibleOrderEntity = await permissions.FilterReadableEntityAsync(
                tenant,
                member,
                orderEntity);
            var relationOptions = await records.ListRelationOptionsAsync(
                tenant,
                member,
                visibleOrderEntity);
            relationOptions["customer_id"].Should().ContainSingle();
            relationOptions["customer_id"][0].Label.Should().Be($"#{customer["id"]}");
            relationOptions["customer_id"][0].SecondaryLabel.Should().BeNull();

            var collisionSecret = $"LABEL-COLLISION-{suffix}";
            await PersistDeclaredLabelCollisionAsync(
                factory,
                tenant,
                Convert.ToInt64(order["id"]),
                collisionSecret);
            var ownerCollisionRecord = await records.GetRecordAsync(
                tenant,
                "order",
                Convert.ToInt64(order["id"]));
            ownerCollisionRecord!["name__label"].Should().Be(collisionSecret,
                "the adversarial persisted field must exist before redaction is evaluated");
            var memberCollisionRecord = await records.GetRecordAsync(
                tenant,
                member,
                "order",
                Convert.ToInt64(order["id"]));
            memberCollisionRecord.Should().NotContainKey("name__label");
            memberCollisionRecord.Should().NotContainValue(collisionSecret);

            var sessionSecret = $"field-read-isolation-{suffix}";
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
            var primaryReaderCookie = RuntimeHttpTestHelper.CreateSessionCookie(
                sessionSecret,
                tenant.TenantStableId,
                tenant.Host,
                primaryReader.UserId,
                primaryReader.Email,
                primaryReader.Role);

            using (var metadataRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/api/runtime/entities/customer",
                       tenant.Host,
                       cookie))
            {
                var metadataResponse = await client.SendAsync(metadataRequest);
                metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
                metadata.RootElement.GetProperty("displayPrimaryField").GetString().Should().Be("id");
                metadata.RootElement.GetProperty("displaySecondaryField").ValueKind.Should().Be(JsonValueKind.Null);
                metadata.RootElement.GetProperty("fields").EnumerateArray()
                    .Select(field => field.GetProperty("name").GetString())
                    .Should().Equal("public_code");
                metadata.RootElement.GetRawText().Should().NotContain("\"name__label\"");
            }

            using (var fallbackLookupRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/api/runtime/entities/customer/lookup",
                       tenant.Host,
                       cookie))
            {
                var fallbackLookupResponse = await client.SendAsync(fallbackLookupRequest);
                fallbackLookupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                using var lookup = JsonDocument.Parse(await fallbackLookupResponse.Content.ReadAsStringAsync());
                lookup.RootElement.GetArrayLength().Should().Be(1);
                var item = lookup.RootElement[0];
                item.GetProperty("label").GetString().Should().Be($"#{customer["id"]}");
                item.GetProperty("secondaryLabel").ValueKind.Should().Be(JsonValueKind.Null);
            }

            using (var partialLookupRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/api/runtime/entities/customer/lookup",
                       tenant.Host,
                       primaryReaderCookie))
            {
                var partialLookupResponse = await client.SendAsync(partialLookupRequest);
                partialLookupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                using var lookup = JsonDocument.Parse(await partialLookupResponse.Content.ReadAsStringAsync());
                lookup.RootElement.GetArrayLength().Should().Be(1);
                var item = lookup.RootElement[0];
                item.GetProperty("label").GetString().Should().Be(customerName);
                item.GetProperty("secondaryLabel").ValueKind.Should().Be(JsonValueKind.Null);
            }

            using var searchRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get,
                $"/api/runtime/entities/order/records?q={Uri.EscapeDataString(hiddenNeedle)}",
                tenant.Host,
                cookie);
            var searchResponse = await client.SendAsync(searchRequest);
            searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var payload = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync()))
            {
                payload.RootElement.GetProperty("total").GetInt64().Should().Be(0);
                payload.RootElement.GetProperty("records").GetArrayLength().Should().Be(0);
                payload.RootElement.GetRawText().Should().NotContain(collisionSecret);
            }

            using var sortRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get,
                "/api/runtime/entities/order/records?sortBy=secret",
                tenant.Host,
                cookie);
            (await client.SendAsync(sortRequest)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            foreach (var path in new[]
                     {
                         "/entities/order/new",
                         $"/entities/order/{order["id"]}"
                     })
            {
                using var pageRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                    HttpMethod.Get,
                    path,
                    tenant.Host,
                    cookie);
                var pageResponse = await client.SendAsync(pageRequest);
                pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                var html = await pageResponse.Content.ReadAsStringAsync();
                html.Should().Contain($"#{customer["id"]}");
                html.Should().NotContain(customerName);
                html.Should().NotContain(customerEmail);
                html.Should().NotContain(collisionSecret);
            }
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
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
            "Restricted operator",
            [role],
            null,
            TimeSpan.FromDays(1));
        await users.ActivateInvitedUserAsync(
            tenant,
            invite.ActivationToken!,
            email,
            "Restricted operator",
            "MemberPass123!");
        var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, "MemberPass123!");
        return new RuntimeUserContext(login!.Id, login.Email, login.Role) { Roles = login.Roles };
    }

    private static async Task PersistDeclaredLabelCollisionAsync(
        ITenantDatabaseConnectionFactory factory,
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        long recordId,
        string secret)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update app.orders set "name__label" = @secret where id = @recordId;
            """;
        command.Parameters.AddWithValue("secret", secret);
        command.Parameters.AddWithValue("recordId", recordId);
        await command.ExecuteNonQueryAsync();
    }

    private static ErpBundle Bundle() => new()
    {
        Id = "field_read_isolation",
        Name = "Field read isolation",
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
                        DisplayName = "Customer",
                        Display = new ErpEntityDisplay { Primary = "name", Secondary = "email" },
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                            new ErpField { Id = "email", Name = "email", DisplayName = "Email", Type = "string", Required = true },
                            new ErpField { Id = "public_code", Name = "public_code", DisplayName = "Public code", Type = "string", Required = true }
                        ]
                    },
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
                            new ErpField { Id = "secret", Name = "secret", DisplayName = "Secret", Type = "string" },
                            new ErpField { Id = "customer", Name = "customer_id", DisplayName = "Customer", Type = "relation", Required = true, References = "customer" }
                        ]
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["restricted"] = new()
            {
                DisplayName = "Restricted",
                Grants =
                [
                    Entity("read", "order"),
                    Entity("create", "order"),
                    Entity("update", "order"),
                    Entity("read", "customer"),
                    Field("read", "order", "name"),
                    Field("read", "order", "status"),
                    Field("read", "order", "customer_id"),
                    Field("write", "order", "name"),
                    Field("write", "order", "status"),
                    Field("write", "order", "customer_id"),
                    Field("read", "customer", "public_code"),
                    Page("entity_list"),
                    Page("entity_create"),
                    Page("entity_detail")
                ]
            },
            ["primary_reader"] = new()
            {
                DisplayName = "Primary reader",
                Grants =
                [
                    Entity("read", "customer"),
                    Field("read", "customer", "name")
                ]
            }
        }
    };

    private static CompiledBundle WithAuthoritativeLabelCollision(CompiledBundle compiled)
    {
        // Model a legacy compiled contract that predates the reserved relation-label suffix.
        // Both active and persisted metadata must agree so this test exercises read isolation,
        // rather than failing earlier on the runtime metadata-integrity boundary.
        var field = new FieldDefinition(
            "label_collision",
            "name__label",
            "Label collision",
            "string",
            "name__label",
            "text",
            false,
            false,
            null,
            [],
            null);
        return compiled with
        {
            Entities = compiled.Entities.Select(entity => entity.Id == "order"
                ? entity with { Fields = [.. entity.Fields, field] }
                : entity).ToArray(),
            Tables = compiled.Tables.Select(table => table.TableName == "orders"
                ? table with { Columns = [.. table.Columns, new ColumnPlan("name__label", "text", true)] }
                : table).ToArray()
        };
    }

    private static ErpGrant Entity(string action, string entity) => new()
    {
        Action = action,
        Entity = entity
    };

    private static ErpGrant Field(string action, string entity, string target) => new()
    {
        Scope = PermissionGrantScopes.Field,
        Action = action,
        Entity = entity,
        Target = target
    };

    private static ErpGrant Page(string target) => new()
    {
        Scope = PermissionGrantScopes.Page,
        Action = "view",
        Entity = "order",
        Target = target
    };
}
