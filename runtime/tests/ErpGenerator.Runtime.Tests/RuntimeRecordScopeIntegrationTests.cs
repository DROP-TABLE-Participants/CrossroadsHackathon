using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeRecordScopeIntegrationTests(
    RuntimeRecordScopeDatabaseFixture fixture) :
    IClassFixture<RuntimeRecordScopeDatabaseFixture>
{
    [PostgresFact]
    public async Task Read_scopes_filter_before_pagination_count_lookup_and_relation_labels()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.ResetScenarioAsync();
        await fixture.SeedHiddenWorkItemsAsync(101);
        var actorOwned = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "Actor visible");
        var directLinked = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.LinkedEmployeeId,
            fixture.OtherWarehouseId,
            "Direct visible");
        var oneHopLinked = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.LinkedWarehouseId,
            "One hop visible");
        var denied = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "Denied secret row");

        var firstPage = await fixture.Records.ListRecordsAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            new RuntimeRecordQuery(Page: 1, PageSize: 2, SortBy: "id", SortDirection: "asc"));
        var secondPage = await fixture.Records.ListRecordsAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            new RuntimeRecordQuery(Page: 2, PageSize: 2, SortBy: "id", SortDirection: "asc"));
        var visibleIds = firstPage.Records.Concat(secondPage.Records)
            .Select(record => Convert.ToInt64(record["id"]))
            .ToArray();

        firstPage.Total.Should().Be(3);
        firstPage.Records.Should().HaveCount(2,
            "record scope must be applied before LIMIT/OFFSET, not after fetching hidden rows");
        visibleIds.Should().BeEquivalentTo(
        [
            Convert.ToInt64(actorOwned["id"]),
            Convert.ToInt64(directLinked["id"]),
            Convert.ToInt64(oneHopLinked["id"])
        ]);
        visibleIds.Should().NotContain(Convert.ToInt64(denied["id"]));

        var counts = await fixture.Records.CountRecordsAsync(
            fixture.Tenant,
            fixture.Member,
            [firstPage.Entity]);
        counts[firstPage.Entity.Key].Should().Be(3);
        var lookup = await fixture.Records.LookupRecordsAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            "visible",
            50);
        lookup.Should().HaveCount(3);
        lookup.Should().NotContain(item => item.Label.Contains("Denied", StringComparison.Ordinal));

        var actorRecord = await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            Convert.ToInt64(actorOwned["id"]));
        actorRecord.Should().NotBeNull();
        actorRecord!["employee_id__label"].Should().Be($"#{fixture.OtherEmployeeId}");
        actorRecord.Values.Should().NotContain(value =>
            string.Equals(Convert.ToString(value), fixture.OtherEmployeeName, StringComparison.Ordinal));

        var directRecord = await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            Convert.ToInt64(directLinked["id"]));
        directRecord!["employee_id__label"].Should().Be(fixture.LinkedEmployeeName);

        var entity = await fixture.Records.GetEntityAsync(fixture.Tenant, "work_items");
        var options = await fixture.Records.ListRelationOptionsAsync(
            fixture.Tenant,
            fixture.Member,
            entity!);
        options["employee_id"].Select(item => item.Id).Should().Equal(fixture.LinkedEmployeeId);
        options["warehouse_id"].Select(item => item.Id).Should().Equal(fixture.LinkedWarehouseId);
    }

    [PostgresFact]
    public async Task Linked_record_scopes_deny_null_missing_archived_and_dangling_links()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.ResetScenarioAsync();
        var direct = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.LinkedEmployeeId,
            fixture.OtherWarehouseId,
            "Direct link");
        var oneHop = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.LinkedWarehouseId,
            "One hop link");
        var nullScopedId = await fixture.InsertNullScopedWorkItemAsync("Null scope");
        var directId = Convert.ToInt64(direct["id"]);
        var oneHopId = Convert.ToInt64(oneHop["id"]);

        (await fixture.VisibleAsync(directId)).Should().BeTrue();
        (await fixture.VisibleAsync(oneHopId)).Should().BeTrue();
        (await fixture.VisibleAsync(nullScopedId)).Should().BeFalse();

        await fixture.SetMemberLinkAsync(recordId: null);
        (await fixture.VisibleAsync(directId)).Should().BeFalse();
        (await fixture.VisibleAsync(oneHopId)).Should().BeFalse();

        await fixture.SetMemberLinkAsync(9_999_999L);
        (await fixture.VisibleAsync(directId)).Should().BeFalse();
        (await fixture.VisibleAsync(oneHopId)).Should().BeFalse();

        await fixture.SetMemberLinkAsync(fixture.LinkedEmployeeId);
        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "employees",
            fixture.LinkedEmployeeId)).Should().BeTrue();
        (await fixture.VisibleAsync(directId)).Should().BeFalse();
        (await fixture.VisibleAsync(oneHopId)).Should().BeFalse();
        (await fixture.Records.RestoreRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "employees",
            fixture.LinkedEmployeeId)).Should().BeTrue();
        await fixture.SetMemberLinkAsync(fixture.LinkedEmployeeId);
    }

    [PostgresFact]
    public async Task Crud_enforces_old_and_new_scope_atomically_and_denied_create_leaves_no_row_or_audit()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.ResetScenarioAsync();
        var owned = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "Owned mutation");
        var foreign = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "Foreign mutation");
        var ownedId = Convert.ToInt64(owned["id"]);
        var foreignId = Convert.ToInt64(foreign["id"]);

        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            foreignId)).Should().BeNull();
        (await fixture.Records.UpdateRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            foreignId,
            RuntimeJson.JsonObject(("name", "Should not update")))).Should().BeNull();
        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            foreignId)).Should().BeFalse();

        var transfer = await fixture.Records.UpdateRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            ownedId,
            RuntimeJson.JsonObject(("owner_principal", fixture.Owner.UserId)));
        transfer.Should().BeNull("the candidate new state leaves the actor's record scope");
        var unchanged = await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "work_items",
            ownedId);
        Convert.ToInt64(unchanged!["owner_principal"]).Should().Be(fixture.Member.UserId);

        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            ownedId)).Should().BeTrue();
        (await fixture.Records.RestoreRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            ownedId)).Should().BeTrue();
        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "work_items",
            foreignId)).Should().BeTrue();
        (await fixture.Records.RestoreRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            foreignId)).Should().BeFalse();

        var beforeRows = await fixture.WorkItemCountAsync();
        var beforeAudits = await fixture.AuditCountAsync("record.created");
        var deniedCreate = () => fixture.Records.CreateRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            fixture.WorkItemValues(
                fixture.Owner.UserId,
                fixture.LinkedEmployeeId,
                fixture.LinkedWarehouseId,
                "Denied create"));
        await deniedCreate.Should().ThrowAsync<RuntimeRecordPermissionException>();
        (await fixture.WorkItemCountAsync()).Should().Be(beforeRows);
        (await fixture.AuditCountAsync("record.created")).Should().Be(beforeAudits);

        var allowed = await fixture.Records.CreateRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            fixture.WorkItemValues(
                fixture.Member.UserId,
                fixture.LinkedEmployeeId,
                fixture.LinkedWarehouseId,
                "Allowed create"));
        Convert.ToInt64(allowed["owner_principal"]).Should().Be(fixture.Member.UserId);

        var createOnly = new RuntimeUserContext(-45_001, "creator-only@record-scope.test", "creator_only")
        {
            Roles = ["creator_only"]
        };
        var minimalOutput = await fixture.Records.CreateRecordAsync(
            fixture.Tenant,
            createOnly,
            "work_items",
            fixture.WorkItemValues(
                createOnly.UserId,
                fixture.LinkedEmployeeId,
                fixture.LinkedWarehouseId,
                "Create without read"));
        minimalOutput.Keys.Should().ContainSingle().Which.Should().Be(
            "id",
            "mutation output must not expose a row that the actor cannot read");

        var blindCreator = new RuntimeUserContext(-45_002, "blind-creator@record-scope.test", "blind_creator")
        {
            Roles = ["blind_creator"]
        };
        var beforeBlindRows = await fixture.WorkItemCountAsync();
        var beforeBlindAudits = await fixture.AuditCountAsync("record.created");
        foreach (var employeeId in new[] { fixture.LinkedEmployeeId, 9_999_999L })
        {
            var blindCreate = () => fixture.Records.CreateRecordAsync(
                fixture.Tenant,
                blindCreator,
                "work_items",
                fixture.WorkItemValues(
                    blindCreator.UserId,
                    employeeId,
                    fixture.LinkedWarehouseId,
                    "Blind relation create"));
            await blindCreate.Should().ThrowAsync<RuntimeRecordPermissionException>(
                "existing and missing target IDs must be indistinguishable without explicit target read capability");
        }
        (await fixture.WorkItemCountAsync()).Should().Be(beforeBlindRows);
        (await fixture.AuditCountAsync("record.created")).Should().Be(beforeBlindAudits);
    }

    [PostgresFact]
    public async Task Durable_multi_role_union_honors_an_explicit_unscoped_grant_but_ignores_forged_session_roles()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.ResetScenarioAsync();
        var hidden = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "Broad role row");
        var hiddenId = Convert.ToInt64(hidden["id"]);
        (await fixture.VisibleAsync(hiddenId)).Should().BeFalse();

        await fixture.AddMemberRoleAsync("broad_reader");
        try
        {
            (await fixture.VisibleAsync(hiddenId)).Should().BeTrue(
                "an explicit unscoped entity read grant deliberately broadens the role union");
        }
        finally
        {
            await fixture.RemoveMemberRoleAsync("broad_reader");
        }

        var forged = fixture.Member with { Roles = ["broad_reader"] };
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            forged,
            "work_items",
            hiddenId)).Should().BeNull(
            "durable runtime_user_roles must override caller-supplied session roles");
    }

    [PostgresFact]
    public async Task Malformed_persisted_record_scope_metadata_is_skipped_or_denied_never_unscoped()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.ResetScenarioAsync();
        var hidden = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "Malformed grant hidden row");
        var hiddenId = Convert.ToInt64(hidden["id"]);
        var original = await fixture.ReadRoleGrantsAsync("member");
        var malformed = new[]
        {
            """[{"Action":"read","Entity":"work_item","Scope":"record","Target":"","RecordScope":{"Field":"owner_principal","Source":"actor_principal","Unexpected":"x"}}]""",
            """[{"Action":"read","Entity":"work_item","Scope":"record","Target":"","RecordScope":{"Field":1,"Source":"actor_principal"}}]""",
            """[{"Action":"read","Entity":"work_item","Scope":"record","Target":"","RecordScope":{"Field":"owner_principal;drop","Source":"actor_principal"}}]""",
            """[{"Action":"read","Entity":"work_item","Scope":"record","Target":""}]""",
            """[{"Action":"read","Entity":"work_item","Scope":1,"Target":""}]""",
            """[{"Action":"read","Entity":"work_item","Scope":"record","Target":"","Unknown":"entity","RecordScope":{"Field":"owner_principal","Source":"actor_principal"}}]""",
            """[{"Action":"read","Entity":"work_item","Scope":"record","Target":"","RecordScope":{"Field":"unknown_field","Source":"actor_principal"}}]"""
        };

        try
        {
            foreach (var grants in malformed)
            {
                await fixture.WriteRoleGrantsAsync("member", grants);
                try
                {
                    (await fixture.Records.GetRecordAsync(
                        fixture.Tenant,
                        fixture.Member,
                        "work_items",
                        hiddenId)).Should().BeNull();
                }
                catch (RuntimeRecordPermissionException)
                {
                    // A malformed grant can remove the entity capability or
                    // produce an explicit deny predicate; both fail closed.
                }
            }
        }
        finally
        {
            await fixture.WriteRoleGrantsAsync("member", original);
        }
    }

    [PostgresFact]
    public async Task Public_store_requires_restore_capability_before_any_include_archived_read_or_count()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.ResetScenarioAsync();
        var reader = new RuntimeUserContext(-46_001, "reader-only@record-scope.test", "reader_only")
        {
            Roles = ["reader_only"]
        };
        var archived = await fixture.CreateWorkItemAsync(
            reader.UserId,
            fixture.OtherEmployeeId,
            fixture.OtherWarehouseId,
            "Archived reader row");
        var archivedId = Convert.ToInt64(archived["id"]);
        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "work_items",
            archivedId)).Should().BeTrue();

        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            reader,
            "work_items",
            archivedId)).Should().BeNull();
        var getArchived = () => fixture.Records.GetRecordAsync(
            fixture.Tenant,
            reader,
            "work_items",
            archivedId,
            includeArchived: true);
        await getArchived.Should().ThrowAsync<RuntimeRecordPermissionException>();
        var listArchived = () => fixture.Records.ListRecordsAsync(
            fixture.Tenant,
            reader,
            "work_items",
            new RuntimeRecordQuery(IncludeArchived: true));
        await listArchived.Should().ThrowAsync<RuntimeRecordPermissionException>();
        var entity = await fixture.Records.GetEntityAsync(fixture.Tenant, "work_items");
        var countArchived = () => fixture.Records.CountRecordsAsync(
            fixture.Tenant,
            reader,
            [entity!],
            includeArchived: true);
        await countArchived.Should().ThrowAsync<RuntimeRecordPermissionException>();

        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            "work_items",
            archivedId,
            includeArchived: true)).Should().NotBeNull(
            "the explicit internal system-owner helper remains available for trusted cleanup and tests");

        var memberArchived = await fixture.CreateWorkItemAsync(
            fixture.Member.UserId,
            fixture.LinkedEmployeeId,
            fixture.LinkedWarehouseId,
            "Restorable member row");
        var memberArchivedId = Convert.ToInt64(memberArchived["id"]);
        (await fixture.Records.ArchiveRecordAsync(
            fixture.Tenant,
            fixture.Owner,
            "work_items",
            memberArchivedId)).Should().BeTrue();
        (await fixture.Records.GetRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            memberArchivedId,
            includeArchived: true)).Should().NotBeNull();
    }

    [PostgresFact]
    public async Task Relation_validation_locks_the_exact_target_and_link_path_until_source_commit()
    {
        if (!fixture.Available)
        {
            return;
        }

        await fixture.ResetScenarioAsync();
        var target = await fixture.CreateWorkItemAsync(
            fixture.Owner.UserId,
            fixture.LinkedEmployeeId,
            fixture.OtherWarehouseId,
            "Locked relation target");
        var targetId = Convert.ToInt64(target["id"]);
        await fixture.InstallRelationPauseTriggerAsync();
        await using var blocker = new NpgsqlConnection(fixture.Factory.CreateConnectionString(fixture.Tenant));
        await blocker.OpenAsync();
        await using (var acquire = blocker.CreateCommand())
        {
            acquire.CommandText = "select pg_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", RuntimeRecordScopeDatabaseFixture.RelationPauseAdvisoryKey);
            await acquire.ExecuteScalarAsync();
        }

        var createTask = fixture.Records.CreateRecordAsync(
            fixture.Tenant,
            fixture.Member,
            "work_items",
            RuntimeJson.JsonObject(
                ("name", "Source waiting after relation validation"),
                ("owner_principal", fixture.Member.UserId),
                ("employee_id", fixture.LinkedEmployeeId),
                ("warehouse_id", fixture.LinkedWarehouseId),
                ("related_item_id", targetId),
                ("status", "open")));
        var targetBlocked = false;
        var linkBlocked = false;
        var linkedRecordBlocked = false;
        try
        {
            await fixture.WaitForPausedInsertAsync();
            targetBlocked = await fixture.UpdateBlocksAsync(
                "update app.work_items set name = name where id = @id",
                command => command.Parameters.AddWithValue("id", targetId));
            linkBlocked = await fixture.UpdateBlocksAsync(
                """
                update _runtime.runtime_user_links
                set created_at = created_at
                where runtime_user_id = @userId and entity_key = 'employee'
                """,
                command => command.Parameters.AddWithValue("userId", fixture.Member.UserId));
            linkedRecordBlocked = await fixture.UpdateBlocksAsync(
                "update app.employees set name = name where id = @id",
                command => command.Parameters.AddWithValue("id", fixture.LinkedEmployeeId));

            await fixture.ReleaseRelationPauseAsync(blocker);
            (await createTask).Should().ContainKey("id");
        }
        finally
        {
            await fixture.ReleaseRelationPauseAsync(blocker);
            if (!createTask.IsCompleted)
            {
                await createTask;
            }

            await fixture.DropRelationPauseTriggerAsync();
        }

        targetBlocked.Should().BeTrue("the exact referenced target row must be held FOR SHARE");
        linkBlocked.Should().BeTrue("the exact runtime_user_links row must be held FOR SHARE");
        linkedRecordBlocked.Should().BeTrue("the actor-linked record used by the scope must be held FOR SHARE");
    }
}

public sealed class RuntimeRecordScopeDatabaseFixture : IAsyncLifetime
{
    public const int RelationPauseAdvisoryKey = 1_886_542_903;

    private string? adminConnectionString;
    private string? databaseName;

    public bool Available { get; private set; }
    public ResolvedTenantEnvironment Tenant { get; private set; } = null!;
    public RuntimeRecordStore Records { get; private set; } = null!;
    public RuntimeUserStore Users { get; private set; } = null!;
    public RuntimeUserContext Owner { get; private set; } = null!;
    public RuntimeUserContext Member { get; private set; } = null!;
    public TenantDatabaseConnectionFactory Factory { get; private set; } = null!;
    public long LinkedWarehouseId { get; private set; }
    public long OtherWarehouseId { get; private set; }
    public long LinkedEmployeeId { get; private set; }
    public long OtherEmployeeId { get; private set; }
    public string LinkedEmployeeName { get; private set; } = string.Empty;
    public string OtherEmployeeName { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = TestPostgres.GetRequiredAdminConnectionString();
        adminConnectionString = connectionString;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        databaseName = $"erp_record_scope_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        Tenant = RuntimeTestTenant.Create("record-scope", "record-scope.erpgenerator.example", databaseName);
        Factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
        Records = new RuntimeRecordStore(Factory);
        Users = new RuntimeUserStore(Factory);
        var runtimeDatabase = new RuntimeDatabase(Factory, new RuntimeSqlBuilder());
        await runtimeDatabase.ApplyCompiledBundleAsync(Tenant, new ErpBundleCompiler().Compile(CreateBundle()));
        Owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
            runtimeDatabase,
            Tenant,
            $"owner-{suffix}@record-scope.test");

        await CreateReferenceRecordsAsync(viewerPrincipal: Owner.UserId);
        Member = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(
            Users,
            runtimeDatabase,
            Tenant,
            Owner,
            $"member-{suffix}@record-scope.test",
            LinkedEmployeeId);
        await SetReferenceViewersAsync(Member.UserId);
        Available = true;
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(adminConnectionString) && !string.IsNullOrWhiteSpace(databaseName))
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    public async Task ResetScenarioAsync()
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using (var truncate = connection.CreateCommand())
        {
            truncate.CommandText = "truncate table app.work_items, app.employees, app.warehouses restart identity cascade";
            await truncate.ExecuteNonQueryAsync();
        }

        await CreateReferenceRecordsAsync(Member.UserId);
        await SetMemberLinkAsync(LinkedEmployeeId);
    }

    public Dictionary<string, System.Text.Json.JsonElement> WorkItemValues(
        long ownerPrincipal,
        long employeeId,
        long warehouseId,
        string name) => RuntimeJson.JsonObject(
        ("name", name),
        ("owner_principal", ownerPrincipal),
        ("employee_id", employeeId),
        ("warehouse_id", warehouseId),
        ("status", "open"));

    public Task<Dictionary<string, object?>> CreateWorkItemAsync(
        long ownerPrincipal,
        long employeeId,
        long warehouseId,
        string name) => Records.CreateRecordAsync(
        Tenant,
        Owner,
        "work_items",
        WorkItemValues(ownerPrincipal, employeeId, warehouseId, name));

    public async Task<bool> VisibleAsync(long recordId) =>
        await Records.GetRecordAsync(Tenant, Member, "work_items", recordId) is not null;

    public async Task SetMemberLinkAsync(long? recordId)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        if (recordId is null)
        {
            command.CommandText = "delete from _runtime.runtime_user_links where runtime_user_id = @userId and entity_key = 'employee'";
        }
        else
        {
            command.CommandText = """
                insert into _runtime.runtime_user_links (runtime_user_id, entity_key, record_id, created_by)
                values (@userId, 'employee', @recordId, @createdBy)
                on conflict (runtime_user_id, entity_key) do update set record_id = excluded.record_id
                """;
            command.Parameters.AddWithValue("recordId", recordId.Value);
            command.Parameters.AddWithValue("createdBy", Owner.UserId);
        }

        command.Parameters.AddWithValue("userId", Member.UserId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> InsertNullScopedWorkItemAsync(string name)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into app.work_items (name, status, created_by, updated_by)
            values (@name, 'open', @owner, @owner)
            returning id
            """;
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("owner", Owner.UserId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task SeedHiddenWorkItemsAsync(int count)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into app.work_items (
              name, owner_principal, employee_id, warehouse_id, status, created_by, updated_by)
            select 'Hidden ' || value::text, @owner, @employee, @warehouse, 'open', @owner, @owner
            from generate_series(1, @count) as value
            """;
        command.Parameters.AddWithValue("owner", Owner.UserId);
        command.Parameters.AddWithValue("employee", OtherEmployeeId);
        command.Parameters.AddWithValue("warehouse", OtherWarehouseId);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> WorkItemCountAsync() =>
        await ScalarCountAsync("select count(*) from app.work_items");

    public async Task<long> AuditCountAsync(string eventType)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from audit.events where event_type = @eventType";
        command.Parameters.AddWithValue("eventType", eventType);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task AddMemberRoleAsync(string role) =>
        (await Users.AddRoleAsync(Tenant, Owner, Member.UserId, role)).Should().BeTrue();

    public async Task RemoveMemberRoleAsync(string role) =>
        (await Users.RemoveRoleAsync(Tenant, Owner, Member.UserId, role)).Should().BeTrue();

    public async Task<string> ReadRoleGrantsAsync(string role)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select grants::text from _runtime.role_definitions where id = @role";
        command.Parameters.AddWithValue("role", role);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public async Task WriteRoleGrantsAsync(string role, string grants)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update _runtime.role_definitions set grants = cast(@grants as jsonb) where id = @role";
        command.Parameters.AddWithValue("grants", grants);
        command.Parameters.AddWithValue("role", role);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    public async Task InstallRelationPauseTriggerAsync()
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            create or replace function app.pause_record_scope_insert()
            returns trigger
            language plpgsql
            as $pause$
            begin
              perform pg_advisory_xact_lock({RelationPauseAdvisoryKey});
              return new;
            end
            $pause$;

            drop trigger if exists pause_record_scope_insert on app.work_items;
            create trigger pause_record_scope_insert
            before insert on app.work_items
            for each row execute function app.pause_record_scope_insert()
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DropRelationPauseTriggerAsync()
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            drop trigger if exists pause_record_scope_insert on app.work_items;
            drop function if exists app.pause_record_scope_insert()
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task WaitForPausedInsertAsync()
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select exists (
                  select 1
                  from pg_locks
                  where locktype = 'advisory' and not granted)
                """;
            if (await command.ExecuteScalarAsync() is true)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Record insert did not reach the advisory trigger pause.");
    }

    public async Task<bool> UpdateBlocksAsync(string sql, Action<NpgsqlCommand> configure)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await command.ExecuteNonQueryAsync(cancellation.Token);
            return false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            return true;
        }
    }

    public async Task ReleaseRelationPauseAsync(NpgsqlConnection blocker)
    {
        if (blocker.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var command = blocker.CreateCommand();
        command.CommandText = "select pg_advisory_unlock(@key)";
        command.Parameters.AddWithValue("key", RelationPauseAdvisoryKey);
        await command.ExecuteScalarAsync();
    }

    private async Task<long> ScalarCountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Factory.CreateConnectionString(Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task CreateReferenceRecordsAsync(long viewerPrincipal)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var linkedWarehouse = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "warehouses",
            RuntimeJson.JsonObject(
                ("name", $"Linked warehouse {suffix}"),
                ("viewer_principal", viewerPrincipal)));
        var otherWarehouse = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "warehouses",
            RuntimeJson.JsonObject(
                ("name", $"Other warehouse {suffix}"),
                ("viewer_principal", Owner.UserId)));
        LinkedWarehouseId = Convert.ToInt64(linkedWarehouse["id"]);
        OtherWarehouseId = Convert.ToInt64(otherWarehouse["id"]);
        LinkedEmployeeName = $"Linked employee {suffix}";
        OtherEmployeeName = $"Other employee {suffix}";
        var linkedEmployee = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "employees",
            RuntimeJson.JsonObject(
                ("name", LinkedEmployeeName),
                ("viewer_principal", viewerPrincipal),
                ("warehouse_id", LinkedWarehouseId)));
        var otherEmployee = await Records.CreateRecordAsync(
            Tenant,
            Owner,
            "employees",
            RuntimeJson.JsonObject(
                ("name", OtherEmployeeName),
                ("viewer_principal", Owner.UserId),
                ("warehouse_id", OtherWarehouseId)));
        LinkedEmployeeId = Convert.ToInt64(linkedEmployee["id"]);
        OtherEmployeeId = Convert.ToInt64(otherEmployee["id"]);
    }

    private async Task SetReferenceViewersAsync(long viewerPrincipal)
    {
        await Records.UpdateRecordAsync(
            Tenant,
            Owner,
            "warehouses",
            LinkedWarehouseId,
            RuntimeJson.JsonObject(("viewer_principal", viewerPrincipal)));
        await Records.UpdateRecordAsync(
            Tenant,
            Owner,
            "employees",
            LinkedEmployeeId,
            RuntimeJson.JsonObject(("viewer_principal", viewerPrincipal)));
    }

    private static ErpBundle CreateBundle() => new()
    {
        Id = "record_scope_store_bundle",
        Name = "Record scope store bundle",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "operations",
                Name = "Operations",
                Entities =
                [
                    Entity("warehouse", "warehouses",
                    [
                        Field("name", "string", required: true),
                        Field("viewer_principal", "integer", required: true)
                    ]),
                    Entity("employee", "employees",
                    [
                        Field("name", "string", required: true),
                        Field("viewer_principal", "integer", required: true),
                        Relation("warehouse", "warehouse_id", "warehouse")
                    ]),
                    Entity("work_item", "work_items",
                    [
                        Field("name", "string", required: true),
                        Field("owner_principal", "integer"),
                        Relation("employee", "employee_id", "employee"),
                        Relation("warehouse", "warehouse_id", "warehouse"),
                        Relation("related_item", "related_item_id", "work_item"),
                        new ErpField
                        {
                            Id = "status",
                            Name = "status",
                            DisplayName = "Status",
                            Type = "enum",
                            Required = true,
                            Options = ["open", "closed"]
                        }
                    ])
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["member"] = new()
            {
                DisplayName = "Member",
                Grants =
                [
                    RecordGrant("read", "work_item", "owner_principal", "actor_principal"),
                    RecordGrant("create", "work_item", "owner_principal", "actor_principal"),
                    RecordGrant("update", "work_item", "owner_principal", "actor_principal"),
                    RecordGrant("archive", "work_item", "owner_principal", "actor_principal"),
                    RecordGrant("restore", "work_item", "owner_principal", "actor_principal"),
                    RecordGrant("read", "work_item", "employee", "linked_record", "employee"),
                    RecordGrant("read", "work_item", "warehouse", "linked_record", "employee", "warehouse"),
                    RecordGrant("read", "employee", "viewer_principal", "actor_principal"),
                    RecordGrant("read", "warehouse", "viewer_principal", "actor_principal")
                ]
            },
            ["broad_reader"] = new()
            {
                DisplayName = "Broad reader",
                Grants = [new ErpGrant { Action = "read", Entity = "work_item" }]
            },
            ["creator_only"] = new()
            {
                DisplayName = "Creator only",
                Grants =
                [
                    RecordGrant("create", "work_item", "owner_principal", "actor_principal"),
                    new ErpGrant { Action = "read", Entity = "employee" },
                    new ErpGrant { Action = "read", Entity = "warehouse" }
                ]
            },
            ["blind_creator"] = new()
            {
                DisplayName = "Blind creator",
                Grants = [RecordGrant("create", "work_item", "owner_principal", "actor_principal")]
            },
            ["reader_only"] = new()
            {
                DisplayName = "Reader only",
                Grants = [RecordGrant("read", "work_item", "owner_principal", "actor_principal")]
            }
        }
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

    private static ErpField Relation(
        string id,
        string column,
        string references) => new()
    {
        Id = id,
        Name = column,
        DisplayName = id.Replace('_', ' '),
        Type = "relation",
        References = references
    };
}
