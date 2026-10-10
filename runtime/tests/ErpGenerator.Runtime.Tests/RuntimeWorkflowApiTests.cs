using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeWorkflowApiTests
{
    [PostgresFact]
    public async Task Unauthorized_task_ids_match_missing_task_responses_and_write_permission_denied_audit_events()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_workflow_api_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var users = new RuntimeUserStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, tenant, "owner@acme.test");
            var memberInvite = await users.InviteUserAsync(tenant, owner, "member@acme.test", "Member", ["member"], null, TimeSpan.FromDays(7));
            (await users.ActivateInvitedUserAsync(tenant, memberInvite.ActivationToken!, "member@acme.test", "Member", "MemberPass123!")).Should().BeTrue();
            var memberLogin = await runtimeDatabase.ValidateLoginAsync(tenant, "member@acme.test", "MemberPass123!");
            memberLogin.Should().NotBeNull();
            var timesheetId = await RuntimeRecordTestHelper.CreateDraftTimesheetAsync(tenant, records, owner, suffix);
            var start = await workflows.StartActionAsync(tenant, owner, "timesheets", timesheetId, "submit_for_approval");

            var sessionSecret = $"workflow-api-test-{suffix}";
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
                        services.AddSingleton<IControlPlaneClient>(new StaticControlPlaneClient(tenant));
                    });
                });
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var memberCookie = RuntimeHttpTestHelper.CreateSessionCookie(sessionSecret, tenant.TenantStableId, tenant.Host, memberLogin!.Id, memberLogin.Email, memberLogin.Role);
            var missingTaskId = start.Task!.Id + 1_000_000;
            foreach (var (method, suffixPath) in new[]
                     {
                         (HttpMethod.Get, string.Empty),
                         (HttpMethod.Get, "/record"),
                         (HttpMethod.Post, "/approve")
                     })
            {
                using var unauthorizedResponse = await SendTaskRequestAsync(
                    client,
                    method,
                    start.Task.Id,
                    suffixPath,
                    tenant.Host,
                    memberCookie);
                using var missingResponse = await SendTaskRequestAsync(
                    client,
                    method,
                    missingTaskId,
                    suffixPath,
                    tenant.Host,
                    memberCookie);

                unauthorizedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
                missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await unauthorizedResponse.Content.ReadAsStringAsync()).Should().Be(
                    await missingResponse.Content.ReadAsStringAsync(),
                    $"{method} task-id probes must not disclose whether the task exists");
            }

            using var workflowDetailRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/runtime/workflow-instances/{start.Instance.Id}");
            workflowDetailRequest.Headers.Host = tenant.Host;
            workflowDetailRequest.Headers.Add("Cookie", memberCookie);
            var workflowDetailResponse = await client.SendAsync(workflowDetailRequest);
            workflowDetailResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var inviteRequest = new HttpRequestMessage(HttpMethod.Post, "/api/runtime/users/invite");
            inviteRequest.Headers.Host = tenant.Host;
            inviteRequest.Headers.Add("Cookie", memberCookie);
            inviteRequest.Content = new StringContent(
                """{"email":"blocked@acme.test","name":"Blocked","roles":["member"]}""",
                Encoding.UTF8,
                "application/json");
            var inviteResponse = await client.SendAsync(inviteRequest);
            inviteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var auditEvents = await records.ListAuditEventsAsync(tenant, 20);
            auditEvents.Should().Contain(auditEvent =>
                auditEvent.EventType == "permission.denied" &&
                auditEvent.Metadata.GetProperty("action").GetString() == "approve_task");
            auditEvents.Should().Contain(auditEvent =>
                auditEvent.EventType == "permission.denied" &&
                auditEvent.Metadata.GetProperty("action").GetString() == "admin");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static async Task<HttpResponseMessage> SendTaskRequestAsync(
        HttpClient client,
        HttpMethod method,
        long taskId,
        string suffixPath,
        string host,
        string cookie)
    {
        using var request = new HttpRequestMessage(method, $"/api/runtime/tasks/{taskId}{suffixPath}");
        request.Headers.Host = host;
        request.Headers.Add("Cookie", cookie);
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }
}
