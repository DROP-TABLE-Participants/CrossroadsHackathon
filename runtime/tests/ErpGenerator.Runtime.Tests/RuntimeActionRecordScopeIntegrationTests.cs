using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeActionRecordScopeIntegrationTests(
    RuntimeActionRecordScopeDatabaseFixture fixture) :
    IClassFixture<RuntimeActionRecordScopeDatabaseFixture>
{
    [PostgresFact]
    public async Task Direct_actor_and_link_scopes_control_action_visibility_and_source_execution()
    {
        if (!fixture.Available)
        {
            return;
        }

        var actorOwned = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Actor owned");
        var directLinked = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.LinkedEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Direct employee link");
        var oneHopLinked = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.LinkedWarehouseId,
            "other",
            "Employee warehouse link");
        var denied = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Denied item");

        (await fixture.Records.GetRecordAsync(
                fixture.Tenant,
                fixture.Member,
                "work_items",
                Convert.ToInt64(actorOwned["id"])))
            .Should().NotBeNull(
                "the central actor-principal predicate must admit the same source row");

        foreach (var allowed in new[] { actorOwned, directLinked, oneHopLinked })
        {
            var id = Convert.ToInt64(allowed["id"]);
            (await fixture.Actions.ListRecordActionsAsync(
                    fixture.Tenant,
                    fixture.Member,
                    "work_items",
                    id))
                .Should().Contain(
                    action => action.Key == "mark_item",
                    "{0} must satisfy one exact source scope",
                    allowed["name"]);
            (await fixture.Actions.ExecuteActionAsync(
                fixture.Tenant,
                fixture.Member,
                "work_items",
                id,
                "mark_item")).Success.Should().BeTrue();
            (await fixture.Records.GetRecordAsync(fixture.Tenant, "work_items", id))!["status"]
                .Should().Be("done");
        }

        var deniedId = Convert.ToInt64(denied["id"]);
        (await fixture.Actions.ListRecordActionsAsync(
                fixture.Tenant,
                fixture.Member,
                "work_items",
                deniedId))
            .Should().NotContain(action => action.Key == "mark_item");
        var executeDenied = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            deniedId,
            "mark_item");
        await executeDenied.Should().ThrowAsync<RuntimeActionRecordNotFoundException>();
        (await fixture.Records.GetRecordAsync(fixture.Tenant, "work_items", deniedId))!["status"]
            .Should().Be("other");
    }

    [PostgresFact]
    public async Task Effect_update_keeps_source_and_query_rows_bounded_by_scoped_read()
    {
        if (!fixture.Available)
        {
            return;
        }

        var ownedSource = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Effect-owned source");
        var hiddenSource = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Effect-hidden source");
        var ownedSourceId = Convert.ToInt64(ownedSource["id"]);
        var hiddenSourceId = Convert.ToInt64(hiddenSource["id"]);

        (await fixture.Actions.ListRecordActionsAsync(
                fixture.Tenant,
                fixture.Member,
                "work_items",
                ownedSourceId))
            .Should().Contain(action => action.Key == "effect_mark_item");
        (await fixture.Actions.ListRecordActionsAsync(
                fixture.Tenant,
                fixture.Member,
                "work_items",
                hiddenSourceId))
            .Should().NotContain(action => action.Key == "effect_mark_item");

        (await fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            ownedSourceId,
            "effect_mark_item")).Success.Should().BeTrue();
        var hiddenSourceExecution = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            hiddenSourceId,
            "effect_mark_item");
        await hiddenSourceExecution.Should().ThrowAsync<RuntimeActionRecordNotFoundException>();
        (await fixture.Records.GetRecordAsync(fixture.Tenant, "work_items", ownedSourceId))!["status"]
            .Should().Be("done");
        (await fixture.Records.GetRecordAsync(fixture.Tenant, "work_items", hiddenSourceId))!["status"]
            .Should().Be("other");

        var visibleQueryRow = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "effect_scoped_bulk",
            "Effect query visible");
        var hiddenQueryRow = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "effect_scoped_bulk",
            "Effect query hidden");
        var batch = await fixture.CreateBatchAsync(
            fixture.Member.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.Member.UserId,
            name: "Effect scoped query batch");

        var queryExecution = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "batches",
            Convert.ToInt64(batch["id"]),
            "effect_scoped_bulk_finish_items");
        var queryDenial = await queryExecution.Should().ThrowAsync<RuntimeActionPermissionException>();
        queryDenial.Which.PermissionAction.Should().Be("read");
        queryDenial.Which.Target.Should().Be("work_item");
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(visibleQueryRow["id"])))!["status"].Should().Be("effect_scoped_bulk");
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(hiddenQueryRow["id"])))!["status"].Should().Be("effect_scoped_bulk");
    }

    [PostgresFact]
    public async Task Source_records_always_require_read_access_even_when_the_action_does_not_declare_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        var target = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Blind creator target");
        var batch = await fixture.CreateBatchAsync(
            fixture.BlindCreator.UserId,
            Convert.ToInt64(target["id"]),
            fixture.BlindCreator.UserId,
            "Blind creator batch");
        var batchId = Convert.ToInt64(batch["id"]);

        (await fixture.Actions.ListRecordActionsAsync(
                fixture.Tenant,
                fixture.BlindCreator,
                "batches",
                batchId))
            .Should().NotContain(action => action.Key == "create_note_without_source_read");

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.BlindCreator,
            "batches",
            batchId,
            "create_note_without_source_read");
        await execute.Should().ThrowAsync<RuntimeActionRecordNotFoundException>();
    }

    [PostgresFact]
    public async Task Source_read_failures_are_non_enumerable_and_leave_no_audit_or_effect()
    {
        if (!fixture.Available)
        {
            return;
        }

        var hidden = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Non-enumerable hidden source");
        var archived = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Non-enumerable archived source");
        var hiddenId = Convert.ToInt64(hidden["id"]);
        var archivedId = Convert.ToInt64(archived["id"]);
        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "work_items",
            archivedId,
            "source non-enumerability test")).Should().BeTrue();
        var blindBatch = await fixture.CreateBatchAsync(
            fixture.BlindCreator.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.BlindCreator.UserId,
            name: "Non-enumerable no-capability source");
        var auditBefore = await fixture.AuditEventCountAsync();
        var notesBefore = await fixture.NoteCountAsync();

        foreach (var execute in new Func<Task>[]
                 {
                     () => fixture.Actions.ExecuteActionAsync(
                         fixture.Tenant,
                         fixture.Member,
                         "work_items",
                         hiddenId,
                         "mark_item"),
                     () => fixture.Actions.ExecuteActionAsync(
                         fixture.Tenant,
                         fixture.Member,
                         "work_items",
                         archivedId,
                         "mark_item"),
                     () => fixture.Actions.ExecuteActionAsync(
                         fixture.Tenant,
                         fixture.Member,
                         "work_items",
                         long.MaxValue,
                         "mark_item"),
                     () => fixture.Actions.ExecuteActionAsync(
                         fixture.Tenant,
                         fixture.BlindCreator,
                         "batches",
                         Convert.ToInt64(blindBatch["id"]),
                         "create_note_without_source_read")
                 })
        {
            await execute.Should().ThrowAsync<RuntimeActionRecordNotFoundException>();
        }

        (await fixture.AuditEventCountAsync()).Should().Be(auditBefore);
        (await fixture.NoteCountAsync()).Should().Be(notesBefore);
        (await fixture.Records.GetRecordAsync(fixture.Tenant, "work_items", hiddenId))!["status"]
            .Should().Be("other");
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            archivedId,
            includeArchived: true))!["archived_at"].Should().NotBeNull();
    }

    [PostgresFact]
    public async Task Action_authorization_uses_durable_role_assignments_not_stale_context_roles()
    {
        if (!fixture.Available)
        {
            return;
        }

        var staleMember = await fixture.CreateMemberAsync(
            $"stale-action-{Guid.NewGuid():N}@action-scope.test");
        staleMember.Roles.Should().Contain("member");
        var source = await fixture.CreateWorkItemAsync(
            staleMember.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Stale action role source");
        var sourceId = Convert.ToInt64(source["id"]);
        await fixture.RemoveAllDurableRolesAsync(staleMember.UserId);
        var auditBefore = await fixture.AuditEventCountAsync();

        (await fixture.Actions.ListRecordActionsAsync(
                fixture.Tenant,
                staleMember,
                "work_items",
                sourceId))
            .Should().NotContain(action => action.Key == "mark_item");
        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            staleMember,
            "work_items",
            sourceId,
            "mark_item");
        var denied = await execute.Should().ThrowAsync<RuntimeActionPermissionException>();
        denied.Which.PermissionAction.Should().Be("named_action.execute");
        denied.Which.Target.Should().Be("mark_item");

        (await fixture.AuditEventCountAsync()).Should().Be(auditBefore + 1);
        (await fixture.Records.GetRecordAsync(fixture.Tenant, "work_items", sourceId))!["status"]
            .Should().Be("other");
    }

    [PostgresFact]
    public async Task Action_route_returns_the_same_404_for_every_source_read_failure()
    {
        if (!fixture.Available)
        {
            return;
        }

        var hidden = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Route hidden source");
        var archived = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Route archived source");
        var archivedId = Convert.ToInt64(archived["id"]);
        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "work_items",
            archivedId,
            "route source non-enumerability test")).Should().BeTrue();
        var blindBatch = await fixture.CreateBatchAsync(
            fixture.BlindCreator.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.BlindCreator.UserId,
            name: "Route no-capability source");
        var staleMember = await fixture.CreateMemberAsync(
            $"stale-route-action-{Guid.NewGuid():N}@action-scope.test");
        var staleSource = await fixture.CreateWorkItemAsync(
            staleMember.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Route stale-role source");
        await fixture.RemoveAllDurableRolesAsync(staleMember.UserId);

        var sessionSecret = $"action-record-scope-{Guid.NewGuid():N}";
        using var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Runtime:SessionSecret"] = sessionSecret,
                        ["RuntimeDatabase:TenantConnectionStringTemplate"] = fixture.AdminConnectionString
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IControlPlaneClient>();
                    services.AddSingleton<IControlPlaneClient>(new MultiTenantControlPlaneClient(fixture.Tenant));
                });
            });
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var memberCookie = RuntimeHttpTestHelper.CreateSessionCookie(
            sessionSecret,
            fixture.Tenant.TenantStableId,
            fixture.Tenant.Host,
            fixture.Member.UserId,
            fixture.Member.Email,
            fixture.Member.Role);
        var blindCookie = RuntimeHttpTestHelper.CreateSessionCookie(
            sessionSecret,
            fixture.Tenant.TenantStableId,
            fixture.Tenant.Host,
            fixture.BlindCreator.UserId,
            fixture.BlindCreator.Email,
            fixture.BlindCreator.Role);
        var staleCookie = RuntimeHttpTestHelper.CreateSessionCookie(
            sessionSecret,
            fixture.Tenant.TenantStableId,
            fixture.Tenant.Host,
            staleMember.UserId,
            staleMember.Email,
            staleMember.Role);
        var auditBefore = await fixture.AuditEventCountAsync();

        var cases = new[]
        {
            (Entity: "work_items", Id: Convert.ToInt64(hidden["id"]), Action: "mark_item", Cookie: memberCookie, Status: HttpStatusCode.NotFound),
            (Entity: "work_items", Id: archivedId, Action: "mark_item", Cookie: memberCookie, Status: HttpStatusCode.NotFound),
            (Entity: "work_items", Id: long.MaxValue, Action: "mark_item", Cookie: memberCookie, Status: HttpStatusCode.NotFound),
            (Entity: "batches", Id: Convert.ToInt64(blindBatch["id"]), Action: "create_note_without_source_read", Cookie: blindCookie, Status: HttpStatusCode.NotFound),
            (Entity: "work_items", Id: Convert.ToInt64(staleSource["id"]), Action: "mark_item", Cookie: staleCookie, Status: HttpStatusCode.Forbidden)
        };
        foreach (var item in cases)
        {
            using var request = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Post,
                $"/api/runtime/entities/{item.Entity}/records/{item.Id}/actions/{item.Action}",
                fixture.Tenant.Host,
                item.Cookie);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(item.Status);
        }

        (await fixture.AuditEventCountAsync()).Should().Be(auditBefore + 1);
    }

    [PostgresFact]
    public async Task Action_queries_apply_record_scope_before_the_bounded_limit()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.SeedQueryLimitItemsAsync(unauthorizedCount: 101);
        var batch = await fixture.CreateBatchAsync(
            fixture.Member.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.Member.UserId,
            name: "Query batch");

        var result = await fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "batches",
            Convert.ToInt64(batch["id"]),
            "count_query_items");

        result.Counts["find_items"].Should().Be(1,
            "the 101 earlier unauthorized rows must be filtered before LIMIT is applied");
    }

    [PostgresFact]
    public async Task Create_postconditions_and_scoped_relation_reads_fail_before_durable_effects()
    {
        if (!fixture.Available)
        {
            return;
        }

        var readable = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Readable relation target");
        var hidden = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Hidden relation target");
        var readableBatch = await fixture.CreateBatchAsync(
            fixture.Member.UserId,
            Convert.ToInt64(readable["id"]),
            fixture.Member.UserId,
            "Readable target batch");
        var hiddenBatch = await fixture.CreateBatchAsync(
            fixture.Member.UserId,
            Convert.ToInt64(hidden["id"]),
            fixture.Member.UserId,
            "Hidden target batch");
        var before = await fixture.NoteCountAsync();

        var deniedCandidate = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "batches",
            Convert.ToInt64(readableBatch["id"]),
            "create_wrong_owner_note");
        await deniedCandidate.Should().ThrowAsync<RuntimeActionPermissionException>();
        (await fixture.NoteCountAsync()).Should().Be(before,
            "a denied create candidate must roll back before record.created is durable");

        var hiddenRelation = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "batches",
            Convert.ToInt64(hiddenBatch["id"]),
            "create_note");
        var hiddenDenial = await hiddenRelation.Should().ThrowAsync<RuntimeActionValidationException>();
        hiddenDenial.Which.Message.Should().Contain("references a missing record");
        (await fixture.NoteCountAsync()).Should().Be(before,
            "a hidden relation must be treated as missing without becoming an existence oracle");
    }

    [PostgresFact]
    public async Task Relation_existence_requires_explicit_read_capability_even_without_a_read_scope()
    {
        if (!fixture.Available)
        {
            return;
        }

        var target = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "No-read relation target");
        var batch = await fixture.CreateBatchAsync(
            fixture.NoRelationReader.UserId,
            Convert.ToInt64(target["id"]),
            fixture.NoRelationReader.UserId,
            "No relation read batch");
        var batchId = Convert.ToInt64(batch["id"]);
        (await fixture.Actions.ListRecordActionsAsync(
                fixture.Tenant,
                fixture.NoRelationReader,
                "batches",
                batchId))
            .Should().Contain(action => action.Key == "create_note",
                "the denial must come from relation access rather than source authorization");
        var before = await fixture.NoteCountAsync();

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.NoRelationReader,
            "batches",
            batchId,
            "create_note");

        var denial = await execute.Should().ThrowAsync<RuntimeActionPermissionException>();
        denial.Which.PermissionAction.Should().Be("read");
        denial.Which.Target.Should().Be("work_item");
        (await fixture.NoteCountAsync()).Should().Be(before);
    }

    [PostgresFact]
    public async Task Updates_validate_locked_old_and_candidate_new_rows()
    {
        if (!fixture.Available)
        {
            return;
        }

        var owned = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "other",
            "Candidate denial");
        var id = Convert.ToInt64(owned["id"]);

        var changeOwner = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            id,
            "change_item_owner");
        await changeOwner.Should().ThrowAsync<RuntimeActionPermissionException>();

        var unchanged = await fixture.Records.GetRecordAsync(fixture.Tenant, "work_items", id);
        Convert.ToInt64(unchanged!["owner_principal"]).Should().Be(fixture.Member.UserId);
        unchanged["status"].Should().Be("other");
    }

    [PostgresFact]
    public async Task Multi_record_update_denial_rolls_back_every_selected_effect()
    {
        if (!fixture.Available)
        {
            return;
        }

        var owned = await fixture.CreateWorkItemAsync(
            fixture.BulkMember.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "bulk",
            "Bulk owned first");
        var denied = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "bulk",
            "Bulk denied second");
        var batch = await fixture.CreateBatchAsync(
            fixture.Owner.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.Owner.UserId,
            name: "Bulk batch");

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.BulkMember,
            "batches",
            Convert.ToInt64(batch["id"]),
            "bulk_finish_items");
        await execute.Should().ThrowAsync<RuntimeActionPermissionException>();

        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(owned["id"])))!["status"].Should().Be("bulk");
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(denied["id"])))!["status"].Should().Be("bulk");
    }

    [PostgresFact]
    public async Task Multi_record_create_denial_rolls_back_every_selected_effect()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.CreateWorkItemAsync(
            fixture.BulkMember.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "create_bulk",
            "Bulk create allowed first");
        await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "create_bulk",
            "Bulk create denied second");
        var batch = await fixture.CreateBatchAsync(
            fixture.Owner.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.Owner.UserId,
            name: "Bulk create batch");
        var before = await fixture.NoteCountAsync();

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.BulkMember,
            "batches",
            Convert.ToInt64(batch["id"]),
            "bulk_create_notes");
        await execute.Should().ThrowAsync<RuntimeActionPermissionException>();

        (await fixture.NoteCountAsync()).Should().Be(before,
            "a denied later create candidate must roll back all earlier creates in the action transaction");
    }

    [PostgresFact]
    public async Task Query_backed_mutations_reject_a_partially_visible_selection()
    {
        if (!fixture.Available)
        {
            return;
        }

        var visible = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "scoped_bulk",
            "Scoped bulk visible");
        var hidden = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "scoped_bulk",
            "Scoped bulk hidden");
        var batch = await fixture.CreateBatchAsync(
            fixture.Member.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.Member.UserId,
            name: "Scoped bulk batch");

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "batches",
            Convert.ToInt64(batch["id"]),
            "scoped_bulk_finish_items");
        var denial = await execute.Should().ThrowAsync<RuntimeActionPermissionException>();
        denial.Which.PermissionAction.Should().Be("read");
        denial.Which.Target.Should().Be("work_item");

        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(visible["id"])))!["status"].Should().Be("scoped_bulk");
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(hidden["id"])))!["status"].Should().Be("scoped_bulk");
    }

    [PostgresFact]
    public async Task Query_backed_mutations_treat_null_scope_predicates_as_hidden_matches()
    {
        if (!fixture.Available)
        {
            return;
        }

        var visible = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "scoped_bulk_null",
            "Scoped bulk null visible");
        var hidden = await fixture.CreateWorkItemAsync(
            ownerPrincipal: null,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "scoped_bulk_null",
            "Scoped bulk null hidden");
        var batch = await fixture.CreateBatchAsync(
            fixture.Member.UserId,
            targetWorkItemId: null,
            noteOwnerPrincipal: fixture.Member.UserId,
            name: "Scoped bulk null batch");

        var execute = () => fixture.Actions.ExecuteActionAsync(
            fixture.Tenant,
            fixture.Member,
            "batches",
            Convert.ToInt64(batch["id"]),
            "scoped_bulk_null_finish_items");
        await execute.Should().ThrowAsync<RuntimeActionPermissionException>();

        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(visible["id"])))!["status"].Should().Be("scoped_bulk_null");
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            Convert.ToInt64(hidden["id"])))!["status"].Should().Be("scoped_bulk_null");
    }
}

public sealed class RuntimeActionRecordScopeContractTests
{
    [Fact]
    public void Action_record_scope_fixture_bundle_compiles()
    {
        var compiled = new ErpBundleCompiler().Compile(
            RuntimeActionRecordScopeDatabaseFixture.CreateBundle());

        compiled.Actions.Select(action => action.Key).Should().Contain(
        [
            "mark_item",
            "effect_mark_item",
            "change_item_owner",
            "count_query_items",
            "create_note",
            "create_note_without_source_read",
            "create_wrong_owner_note",
            "bulk_finish_items",
            "bulk_create_notes",
            "scoped_bulk_finish_items",
            "effect_scoped_bulk_finish_items",
            "scoped_bulk_null_finish_items"
        ]);
    }
}

public sealed class RuntimeActionRecordScopeDatabaseFixture : IAsyncLifetime
{
    private string? adminConnectionString;
    private string? databaseName;

    public bool Available { get; private set; }

    public string AdminConnectionString => adminConnectionString!;

    public ResolvedTenantEnvironment Tenant { get; private set; } = null!;

    public RuntimeDatabase RuntimeDatabase { get; private set; } = null!;

    public TenantDatabaseConnectionFactory Factory { get; private set; } = null!;

    public RuntimeRecordStore Records { get; private set; } = null!;

    public RuntimeActionStore Actions { get; private set; } = null!;

    public RuntimeUserContext Owner { get; private set; } = null!;

    public RuntimeUserContext Member { get; private set; } = null!;

    public RuntimeUserContext BulkMember { get; private set; } = null!;

    public RuntimeUserContext NoRelationReader { get; private set; } = null!;

    public RuntimeUserContext BlindCreator { get; private set; } = null!;

    public long LinkedWarehouseId { get; private set; }

    public long OtherWarehouseId { get; private set; }

    public long LinkedEmployeeId { get; private set; }

    public long OtherEmployeeId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = TestPostgres.GetRequiredAdminConnectionString();
        adminConnectionString = connectionString;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        databaseName = $"erp_action_record_scope_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        Tenant = RuntimeTestTenant.Create("action-scope", "action-scope.erpgenerator.example", databaseName);
        Factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
        RuntimeDatabase = new RuntimeDatabase(Factory, new RuntimeSqlBuilder());
        Records = new RuntimeRecordStore(Factory);
        Actions = new RuntimeActionStore(Factory, new RuntimePermissionService(Factory));
        var users = new RuntimeUserStore(Factory);
        var compiled = new ErpBundleCompiler().Compile(CreateBundle());
        await RuntimeDatabase.ApplyCompiledBundleAsync(Tenant, compiled);
        Owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
            RuntimeDatabase,
            Tenant,
            $"owner-{suffix}@action-scope.test");

        var linkedWarehouse = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "warehouses",
            RuntimeJson.JsonObject(("name", $"Linked warehouse {suffix}")));
        var otherWarehouse = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "warehouses",
            RuntimeJson.JsonObject(("name", $"Other warehouse {suffix}")));
        LinkedWarehouseId = Convert.ToInt64(linkedWarehouse["id"]);
        OtherWarehouseId = Convert.ToInt64(otherWarehouse["id"]);
        var linkedEmployee = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "employees",
            RuntimeJson.JsonObject(
                ("name", $"Linked employee {suffix}"),
                ("warehouse_id", LinkedWarehouseId)));
        var otherEmployee = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "employees",
            RuntimeJson.JsonObject(
                ("name", $"Other employee {suffix}"),
                ("warehouse_id", OtherWarehouseId)));
        LinkedEmployeeId = Convert.ToInt64(linkedEmployee["id"]);
        OtherEmployeeId = Convert.ToInt64(otherEmployee["id"]);

        Member = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(
            users,
            RuntimeDatabase,
            Tenant,
            Owner,
            $"member-{suffix}@action-scope.test",
            LinkedEmployeeId);
        BulkMember = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(
            users,
            RuntimeDatabase,
            Tenant,
            Owner,
            $"bulk-{suffix}@action-scope.test");
        (await users.AddRoleAsync(Tenant, Owner, BulkMember.UserId, "bulk")).Should().BeTrue();
        (await users.RemoveRoleAsync(Tenant, Owner, BulkMember.UserId, "member")).Should().BeTrue();
        NoRelationReader = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(
            users,
            RuntimeDatabase,
            Tenant,
            Owner,
            $"no-relation-{suffix}@action-scope.test");
        (await users.AddRoleAsync(Tenant, Owner, NoRelationReader.UserId, "no_relation_read")).Should().BeTrue();
        (await users.RemoveRoleAsync(Tenant, Owner, NoRelationReader.UserId, "member")).Should().BeTrue();
        BlindCreator = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(
            users,
            RuntimeDatabase,
            Tenant,
            Owner,
            $"blind-creator-{suffix}@action-scope.test");
        (await users.AddRoleAsync(Tenant, Owner, BlindCreator.UserId, "blind_creator")).Should().BeTrue();
        (await users.RemoveRoleAsync(Tenant, Owner, BlindCreator.UserId, "member")).Should().BeTrue();
        Available = true;
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(adminConnectionString) && !string.IsNullOrWhiteSpace(databaseName))
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    public Task<Dictionary<string, object?>> CreateWorkItemAsync(
        long? ownerPrincipal,
        long employeeId,
        long warehouseId,
        string status,
        string name) =>
        Records.CreateRecordAsync(
            Tenant,
            Owner,
            "work_items",
            RuntimeJson.JsonObject(
                ("name", name),
                ("owner_principal", ownerPrincipal),
                ("employee_id", employeeId),
                ("warehouse_id", warehouseId),
                ("status", status)));

    public async Task<Dictionary<string, object?>> CreateBatchAsync(
        long ownerPrincipal,
        long? targetWorkItemId,
        long noteOwnerPrincipal,
        string name)
    {
        var values = RuntimeJson.JsonObject(
            ("name", name),
            ("owner_principal", ownerPrincipal),
            ("note_owner_principal", noteOwnerPrincipal));
        if (targetWorkItemId is not null)
        {
            values["target_work_item_id"] = JsonSerializer.SerializeToElement(targetWorkItemId.Value);
        }

        return await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "batches",
            values);
    }

    public async Task SeedQueryLimitItemsAsync(int unauthorizedCount)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < unauthorizedCount; index++)
        {
            await InsertQueryItemAsync(
                connection,
                transaction,
                Owner.UserId,
                $"Unauthorized query {index:D3}");
        }

        await InsertQueryItemAsync(
            connection,
            transaction,
            Member.UserId,
            "Authorized query final");
        await transaction.CommitAsync();
    }

    public async Task<long> NoteCountAsync() =>
        (await Records.ListRecordsAsync(Tenant, "notes", new RuntimeRecordQuery(PageSize: 100))).Total;

    public async Task<long> AuditEventCountAsync()
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from audit.events", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public Task<RuntimeUserContext> CreateMemberAsync(string email) =>
        RuntimeUserTestHelper.InviteAndActivateMemberAsync(
            new RuntimeUserStore(Factory),
            RuntimeDatabase,
            Tenant,
            Owner,
            email);

    public async Task RemoveAllDurableRolesAsync(long userId)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "delete from _runtime.runtime_user_roles where user_id = @userId",
            connection);
        command.Parameters.AddWithValue("userId", userId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertQueryItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long ownerPrincipal,
        string name)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into app.work_items (
              name,
              owner_principal,
              employee_id,
              warehouse_id,
              status,
              created_by,
              updated_by)
            values (@name, @ownerPrincipal, @employeeId, @warehouseId, 'query', @createdBy, @createdBy)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("ownerPrincipal", ownerPrincipal);
        command.Parameters.AddWithValue("employeeId", OtherEmployeeId);
        command.Parameters.AddWithValue("warehouseId", OtherWarehouseId);
        command.Parameters.AddWithValue("createdBy", Owner.UserId);
        await command.ExecuteNonQueryAsync();
    }

    internal static ErpBundle CreateBundle() => new()
    {
        Id = "action_record_scope_bundle",
        Name = "Action record scope bundle",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "ops",
                Name = "Operations",
                Entities =
                [
                    Entity("warehouse", "warehouses",
                    [
                        Field("name", "name", "string", required: true)
                    ]),
                    Entity("employee", "employees",
                    [
                        Field("name", "name", "string", required: true),
                        Relation("warehouse", "warehouse_id", "warehouse", required: true)
                    ]),
                    Entity("work_item", "work_items",
                    [
                        Field("name", "name", "string", required: true),
                        Field("owner_principal", "owner_principal", "integer", required: false),
                        Relation("employee", "employee_id", "employee", required: true),
                        Relation("warehouse", "warehouse_id", "warehouse", required: true),
                        new ErpField
                        {
                            Id = "status",
                            Name = "status",
                            DisplayName = "Status",
                            Type = "enum",
                            Required = true,
                            Options = ["query", "bulk", "create_bulk", "scoped_bulk", "effect_scoped_bulk", "scoped_bulk_null", "other", "done"]
                        }
                    ]),
                    Entity("batch", "batches",
                    [
                        Field("name", "name", "string", required: true),
                        Field("owner_principal", "owner_principal", "integer", required: true),
                        Relation("target_work_item", "target_work_item_id", "work_item"),
                        Field("note_owner_principal", "note_owner_principal", "integer", required: true)
                    ]),
                    Entity("note", "notes",
                    [
                        Relation("work_item", "work_item_id", "work_item", required: true),
                        Field("owner_principal", "owner_principal", "integer", required: true),
                        Field("message", "message", "string", required: true)
                    ])
                ]
            }
        ],
        Actions =
        [
            UpdateCurrentAction("mark_item", "Mark item", new Dictionary<string, ErpActionValue>
            {
                ["status"] = new() { Value = "done" }
            }),
            UpdateCurrentAction(
                "effect_mark_item",
                "Effect mark item",
                new Dictionary<string, ErpActionValue>
                {
                    ["status"] = new() { Value = "done" }
                },
                "effect_update"),
            UpdateCurrentAction("change_item_owner", "Change item owner", new Dictionary<string, ErpActionValue>
            {
                ["owner_principal"] = new() { Value = 999999L },
                ["status"] = new() { Value = "done" }
            }),
            new ErpAction
            {
                Key = "count_query_items",
                Label = "Count query items",
                Entity = "batch",
                Type = "record_action",
                Requires = Requirements(("read", "batch"), ("read", "work_item")),
                Steps =
                [
                    QueryItemsStep("find_items", "query")
                ]
            },
            CreateNoteAction("create_note", useWrongOwner: false),
            CreateNoteAction("create_note_without_source_read", useWrongOwner: false, requireSourceRead: false),
            CreateNoteAction("create_wrong_owner_note", useWrongOwner: true),
            new ErpAction
            {
                Key = "bulk_finish_items",
                Label = "Bulk finish items",
                Entity = "batch",
                Type = "record_action",
                Requires = Requirements(
                    ("read", "batch"),
                    ("read", "work_item"),
                    ("update", "work_item")),
                Steps =
                [
                    QueryItemsStep("find_bulk_items", "bulk"),
                    new ErpActionStep
                    {
                        Id = "finish_bulk_items",
                        Type = "update_records",
                        RecordsFrom = new ErpActionRecordsFrom { Result = "find_bulk_items" },
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["status"] = new() { Value = "done" }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "scoped_bulk_finish_items",
                Label = "Scoped bulk finish items",
                Entity = "batch",
                Type = "record_action",
                Requires = Requirements(
                    ("read", "batch"),
                    ("read", "work_item"),
                    ("update", "work_item")),
                Steps =
                [
                    QueryItemsStep("find_scoped_bulk_items", "scoped_bulk"),
                    new ErpActionStep
                    {
                        Id = "finish_scoped_bulk_items",
                        Type = "update_records",
                        RecordsFrom = new ErpActionRecordsFrom { Result = "find_scoped_bulk_items" },
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["status"] = new() { Value = "done" }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "effect_scoped_bulk_finish_items",
                Label = "Effect scoped bulk finish items",
                Entity = "batch",
                Type = "record_action",
                Requires = Requirements(
                    ("read", "batch"),
                    ("read", "work_item"),
                    ("effect_update", "work_item")),
                Steps =
                [
                    QueryItemsStep("find_effect_scoped_bulk_items", "effect_scoped_bulk"),
                    new ErpActionStep
                    {
                        Id = "finish_effect_scoped_bulk_items",
                        Type = "update_records",
                        RecordsFrom = new ErpActionRecordsFrom { Result = "find_effect_scoped_bulk_items" },
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["status"] = new() { Value = "done" }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "scoped_bulk_null_finish_items",
                Label = "Scoped bulk null finish items",
                Entity = "batch",
                Type = "record_action",
                Requires = Requirements(
                    ("read", "batch"),
                    ("read", "work_item"),
                    ("update", "work_item")),
                Steps =
                [
                    QueryItemsStep("find_scoped_bulk_null_items", "scoped_bulk_null"),
                    new ErpActionStep
                    {
                        Id = "finish_scoped_bulk_null_items",
                        Type = "update_records",
                        RecordsFrom = new ErpActionRecordsFrom { Result = "find_scoped_bulk_null_items" },
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["status"] = new() { Value = "done" }
                        }
                    }
                ]
            },
            new ErpAction
            {
                Key = "bulk_create_notes",
                Label = "Bulk create notes",
                Entity = "batch",
                Type = "record_action",
                Requires = Requirements(
                    ("read", "batch"),
                    ("read", "work_item"),
                    ("create", "note")),
                Steps =
                [
                    QueryItemsStep("find_bulk_note_items", "create_bulk"),
                    new ErpActionStep
                    {
                        Id = "create_bulk_notes",
                        Type = "create_records_for_each",
                        Entity = "note",
                        ForEach = new ErpActionForEach { Result = "find_bulk_note_items" },
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["work_item"] = new()
                            {
                                ValueFrom = new ErpActionValueFrom { Item = "id" }
                            },
                            ["owner_principal"] = new()
                            {
                                ValueFrom = new ErpActionValueFrom { Item = "owner_principal" }
                            },
                            ["message"] = new() { Value = "Bulk note" }
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
            ["member"] = new()
            {
                DisplayName = "Member",
                Grants = MemberGrants()
            },
            ["bulk"] = new()
            {
                DisplayName = "Bulk operator",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "batch" },
                    new ErpGrant { Action = "read", Entity = "work_item" },
                    RecordGrant("update", "work_item", "owner_principal", "actor_principal"),
                    RecordGrant("create", "note", "owner_principal", "actor_principal"),
                    ActionGrant("batch", "bulk_finish_items"),
                    ActionGrant("batch", "scoped_bulk_finish_items"),
                    ActionGrant("batch", "scoped_bulk_null_finish_items"),
                    ActionGrant("batch", "bulk_create_notes")
                ]
            },
            ["no_relation_read"] = new()
            {
                DisplayName = "No relation read",
                Grants =
                [
                    RecordGrant("read", "batch", "owner_principal", "actor_principal"),
                    RecordGrant("create", "note", "owner_principal", "actor_principal"),
                    ActionGrant("batch", "create_note")
                ]
            },
            ["blind_creator"] = new()
            {
                DisplayName = "Blind creator",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "work_item" },
                    RecordGrant("create", "note", "owner_principal", "actor_principal"),
                    ActionGrant("batch", "create_note_without_source_read")
                ]
            }
        }
    };

    private static List<ErpGrant> MemberGrants() =>
    [
        RecordGrant("read", "batch", "owner_principal", "actor_principal"),
        RecordGrant("read", "work_item", "owner_principal", "actor_principal"),
        RecordGrant("update", "work_item", "owner_principal", "actor_principal"),
        RecordGrant("read", "work_item", "employee", "linked_record", "employee"),
        RecordGrant("update", "work_item", "employee", "linked_record", "employee"),
        RecordGrant("read", "work_item", "warehouse", "linked_record", "employee", "warehouse"),
        RecordGrant("update", "work_item", "warehouse", "linked_record", "employee", "warehouse"),
        RecordGrant("create", "note", "owner_principal", "actor_principal"),
        new ErpGrant { Action = "effect_update", Entity = "work_item" },
        new ErpGrant
        {
            Scope = PermissionGrantScopes.Field,
            Action = GranularPermissionActions.EffectWrite,
            Entity = "work_item",
            Target = "status"
        },
        ActionGrant("work_item", "mark_item"),
        ActionGrant("work_item", "effect_mark_item"),
        ActionGrant("work_item", "change_item_owner"),
        ActionGrant("batch", "count_query_items"),
        ActionGrant("batch", "create_note"),
        ActionGrant("batch", "create_wrong_owner_note"),
        ActionGrant("batch", "scoped_bulk_finish_items"),
        ActionGrant("batch", "effect_scoped_bulk_finish_items"),
        ActionGrant("batch", "scoped_bulk_null_finish_items")
    ];

    private static ErpGrant ActionGrant(string entity, string action) => new()
    {
        Scope = PermissionGrantScopes.Action,
        Action = GranularPermissionActions.Execute,
        Entity = entity,
        Target = action
    };

    private static ErpGrant RecordGrant(
        string action,
        string entity,
        string field,
        string source,
        string linkedEntity = "",
        string linkedField = "") => new()
        {
            Scope = PermissionGrantScopes.Record,
            Action = action,
            Entity = entity,
            RecordScope = new ErpRecordScope
            {
                Field = field,
                Source = source,
                LinkedEntity = linkedEntity,
                LinkedField = linkedField
            }
        };

    private static ErpAction UpdateCurrentAction(
        string key,
        string label,
        Dictionary<string, ErpActionValue> set,
        string mutationAction = "update") => new()
        {
            Key = key,
            Label = label,
            Entity = "work_item",
            Type = "record_action",
            Requires = mutationAction.StartsWith("effect_", StringComparison.OrdinalIgnoreCase)
                ? Requirements(("read", "work_item"), (mutationAction, "work_item"))
                : Requirements((mutationAction, "work_item")),
            Steps =
        [
            new ErpActionStep
            {
                Id = $"{key}_step",
                Type = "update_current_record",
                Set = set
            }
        ]
        };

    private static ErpAction CreateNoteAction(
        string key,
        bool useWrongOwner,
        bool requireSourceRead = true) => new()
    {
        Key = key,
        Label = key.Replace('_', ' '),
        Entity = "batch",
        Type = "record_action",
        Requires = requireSourceRead
            ? Requirements(("read", "batch"), ("create", "note"))
            : Requirements(("create", "note")),
        Steps =
        [
            new ErpActionStep
            {
                Id = $"{key}_step",
                Type = "create_record",
                Entity = "note",
                Values = new Dictionary<string, ErpActionValue>
                {
                    ["work_item"] = new()
                    {
                        ValueFrom = new ErpActionValueFrom { Record = "target_work_item" }
                    },
                    ["owner_principal"] = useWrongOwner
                        ? new ErpActionValue { Value = 999999L }
                        : new ErpActionValue
                        {
                            ValueFrom = new ErpActionValueFrom { Record = "note_owner_principal" }
                        },
                    ["message"] = new() { Value = "Scoped note" }
                }
            }
        ]
    };

    private static ErpActionStep QueryItemsStep(string id, string status) => new()
    {
        Id = id,
        Type = "query_records",
        Entity = "work_item",
        Where =
        [
            new ErpActionFilter { Field = "status", Op = "eq", Value = status }
        ]
    };

    private static ErpActionRequires Requirements(params (string Action, string Entity)[] requirements) => new()
    {
        Permissions = requirements.Select(requirement => new ErpGrant
        {
            Action = requirement.Action,
            Entity = requirement.Entity
        }).ToList()
    };

    private static ErpEntity Entity(
        string id,
        string name,
        List<ErpField> fields) => new()
        {
            Id = id,
            Name = name,
            DisplayName = id.Replace('_', ' '),
            Fields = fields
        };

    private static ErpField Field(
        string id,
        string name,
        string type,
        bool required = false) => new()
        {
            Id = id,
            Name = name,
            DisplayName = id.Replace('_', ' '),
            Type = type,
            Required = required
        };

    private static ErpField Relation(
        string id,
        string name,
        string references,
        bool required = false) => new()
        {
            Id = id,
            Name = name,
            DisplayName = id.Replace('_', ' '),
            Type = "relation",
            References = references,
            Required = required
        };
}
