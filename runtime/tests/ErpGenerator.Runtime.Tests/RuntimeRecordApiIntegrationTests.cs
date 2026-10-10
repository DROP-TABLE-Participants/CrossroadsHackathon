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
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeRecordApiIntegrationTests
{
    [PostgresFact]
    public async Task Relation_lookup_dropdowns_and_labels_are_tenant_safe_and_archive_aware()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_record_api_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, tenant, "owner@acme.test");

            var activeCustomer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("company_name", $"Active Customer {suffix}"),
                ("contact_email", $"active-{suffix}@example.test"),
                ("status", "active")));
            var archivedCustomer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("company_name", $"Archived Customer {suffix}"),
                ("contact_email", $"archived-{suffix}@example.test"),
                ("status", "inactive")));
            await records.ArchiveRecordAsync(tenant, owner, "customers", Convert.ToInt64(archivedCustomer["id"]), "lookup exclusion");

            var employee = await records.CreateRecordAsync(tenant, owner, "employees", RuntimeJson.JsonObject(
                ("full_name", $"Project Manager {suffix}"),
                ("email", $"pm-{suffix}@example.test"),
                ("role", "PM")));
            var project = await records.CreateRecordAsync(tenant, owner, "projects", RuntimeJson.JsonObject(
                ("customer_id", activeCustomer["id"]!),
                ("title", $"Project {suffix}"),
                ("budget", 5000),
                ("starts_on", "2026-07-02"),
                ("project_manager_id", employee["id"]!)));

            await PointProjectAtMissingCustomerAsync(factory, tenant, Convert.ToInt64(project["id"]));

            var sessionSecret = $"record-api-test-{suffix}";
            var controlPlane = new CapturingUsageControlPlaneClient(tenant);
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
                        services.AddSingleton<IControlPlaneClient>(controlPlane);
                    });
                });
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var ownerCookie = RuntimeHttpTestHelper.CreateSessionCookie(sessionSecret, tenant.TenantStableId, tenant.Host, owner.UserId, owner.Email, owner.Role);

            using var createCustomerRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Post, "/api/runtime/entities/customers/records", tenant.Host, ownerCookie);
            createCustomerRequest.Content = JsonContent.Create(new
            {
                company_name = $"Password=super-secret {suffix}",
                contact_email = $"usage-{suffix}@example.test",
                status = "active"
            });
            var createCustomerResponse = await client.SendAsync(createCustomerRequest);
            createCustomerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            controlPlane.UsageEvents.Should().ContainSingle(usage =>
                usage.EventKind == "entity_record_created" &&
                usage.EntityKey == "customers" &&
                usage.Metadata!.ContainsKey("fieldCount"));
            JsonSerializer.Serialize(controlPlane.UsageEvents).Should().NotContain("super-secret");
            JsonSerializer.Serialize(controlPlane.UsageEvents).Should().NotContain("Password=");

            using var unauthenticatedLookup = new HttpRequestMessage(HttpMethod.Get, "/api/runtime/entities/customers/lookup");
            unauthenticatedLookup.Headers.Host = tenant.Host;
            var unauthenticatedResponse = await client.SendAsync(unauthenticatedLookup);
            unauthenticatedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var landingJwtRequest = new HttpRequestMessage(HttpMethod.Get, "/api/runtime/metadata");
            landingJwtRequest.Headers.Host = tenant.Host;
            landingJwtRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "landing-jwt-placeholder");
            var landingJwtResponse = await client.SendAsync(landingJwtRequest);
            landingJwtResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var lookupRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, $"/api/runtime/entities/customers/lookup?q={Uri.EscapeDataString(suffix)}&limit=100", tenant.Host, ownerCookie);
            var lookupResponse = await client.SendAsync(lookupRequest);
            lookupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var lookupJson = JsonDocument.Parse(await lookupResponse.Content.ReadAsStringAsync());
            var lookupItems = lookupJson.RootElement.EnumerateArray().ToArray();
            lookupItems.Should().Contain(item =>
                item.GetProperty("label").GetString() == $"Active Customer {suffix}" &&
                item.GetProperty("secondaryLabel").GetString() == $"active-{suffix}@example.test");
            lookupItems.Should().NotContain(item => item.GetProperty("label").GetString() == $"Archived Customer {suffix}");

            using var formRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/entities/projects/new", tenant.Host, ownerCookie);
            var formResponse = await client.SendAsync(formRequest);
            formResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var formHtml = await formResponse.Content.ReadAsStringAsync();
            formHtml.Should().Contain("data-relation-autocomplete");
            formHtml.Should().Contain("data-lookup-url=\"/api/runtime/entities/customers/lookup\"");
            formHtml.Should().Contain("name=\"customer_id\" type=\"hidden\"");
            formHtml.Should().NotContain($"Archived Customer {suffix} - archived-{suffix}@example.test");

            using var projectsApiRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/entities/projects/records?sortBy=title&sortDirection=asc&pageSize=10", tenant.Host, ownerCookie);
            var projectsApiResponse = await client.SendAsync(projectsApiRequest);
            projectsApiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var projectsJson = JsonDocument.Parse(await projectsApiResponse.Content.ReadAsStringAsync());
            var projectRecord = projectsJson.RootElement.GetProperty("records").EnumerateArray().Single();
            projectRecord.GetProperty("customer_id__label").GetString().Should().Be("#999999 (missing)");
            projectRecord.GetProperty("project_manager_id__label").GetString().Should().Be($"Project Manager {suffix}");

            using var invalidSortRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/entities/projects/records?sortBy=title;drop table app.projects", tenant.Host, ownerCookie);
            var invalidSortResponse = await client.SendAsync(invalidSortRequest);
            invalidSortResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using var projectsPageRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/entities/projects?q=Project&sortBy=title&sortDirection=asc&pageSize=10", tenant.Host, ownerCookie);
            var projectsPageResponse = await client.SendAsync(projectsPageRequest);
            projectsPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var projectsHtml = await projectsPageResponse.Content.ReadAsStringAsync();
            projectsHtml.Should().Contain("#999999 (missing)");
            projectsHtml.Should().Contain($"Project Manager {suffix}");
            projectsHtml.Should().Contain("data-list-controls");
            projectsHtml.Should().Contain("name=\"sortBy\"");
            projectsHtml.Should().Contain("name=\"pageSize\"");
            projectsHtml.Should().Contain("Active records");
            projectsHtml.Should().Contain("With archived");

            using var archivedPageRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, $"/entities/customers?includeArchived=true&q={Uri.EscapeDataString("Archived Customer " + suffix)}", tenant.Host, ownerCookie);
            var archivedPageResponse = await client.SendAsync(archivedPageRequest);
            archivedPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var archivedHtml = await archivedPageResponse.Content.ReadAsStringAsync();
            archivedHtml.Should().Contain("Archived Customer");
            archivedHtml.Should().Contain("data-row-action=\"restore\"");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static async Task PointProjectAtMissingCustomerAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        long projectId)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update app.projects
            set customer_id = 999999
            where id = @projectId
            """;
        command.Parameters.AddWithValue("projectId", projectId);
        await command.ExecuteNonQueryAsync();
    }

}
