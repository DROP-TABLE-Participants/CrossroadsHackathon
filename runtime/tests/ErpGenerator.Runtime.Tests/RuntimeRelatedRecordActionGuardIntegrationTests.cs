using System.Text.Json;
using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeRelatedRecordActionGuardIntegrationTests
{
    private const int MutationPauseAdvisoryKey = 1_937_421_107;

    [PostgresFact]
    public async Task Related_record_guard_is_identity_exact_and_fails_closed_for_every_read_boundary()
    {
        await WithDatabaseAsync("related_guard", async database =>
        {
            var owner = SystemOwner();
            var @operator = User(-91_001, "operator");
            var approved = await database.Records.CreateRecordAsync(
                database.Tenant,
                owner,
                "formula_revision",
                RuntimeJson.JsonObject(
                    ("name", "Approved formula"),
                    ("status", "approved"),
                    ("owner_principal", owner.UserId)));
            var draft = await database.Records.CreateRecordAsync(
                database.Tenant,
                owner,
                "formula_revision",
                RuntimeJson.JsonObject(
                    ("name", "Selected draft formula"),
                    ("status", "draft"),
                    ("owner_principal", owner.UserId)));

            var approvedBatch = await CreateBatchAsync(database, owner, approved, "Approved batch");
            var draftBatch = await CreateBatchAsync(database, owner, draft, "Draft batch");

            var result = await database.Actions.ExecuteActionAsync(
                database.Tenant,
                @operator,
                "batch",
                Id(approvedBatch),
                RelatedRecordActionGuardTestBundle.ActionKey);

            result.Success.Should().BeTrue();
            result.Counts[RelatedRecordActionGuardTestBundle.QueryStepId].Should().Be(1);
            (await ReadBatchAsync(database, approvedBatch))["status"].Should().Be("released");

            var rejected = () => database.Actions.ExecuteActionAsync(
                database.Tenant,
                @operator,
                "batch",
                Id(draftBatch),
                RelatedRecordActionGuardTestBundle.ActionKey);
            var guard = await rejected.Should().ThrowAsync<RuntimeActionGuardRejectedException>();
            guard.Which.ActionKey.Should().Be(RelatedRecordActionGuardTestBundle.ActionKey);
            guard.Which.GuardStepId.Should().Be(RelatedRecordActionGuardTestBundle.GuardStepId);
            (await ReadBatchAsync(database, draftBatch))["status"].Should().Be("draft",
                "an approved decoy must not satisfy the exact selected-revision identity filter");

            foreach (var (role, permissionAction, target) in new[]
                     {
                         ("no_formula_read", "read", "formula_revision"),
                         ("field_blind", "field.read", "formula_revisions.status"),
                         ("scoped_operator", "read", "formula_revision")
                     })
            {
                var deniedBatch = await CreateBatchAsync(
                    database,
                    owner,
                    approved,
                    $"Denied {role}");
                var execute = () => database.Actions.ExecuteActionAsync(
                    database.Tenant,
                    User(role == "scoped_operator" ? -91_004 : role == "field_blind" ? -91_003 : -91_002, role),
                    "batch",
                    Id(deniedBatch),
                    RelatedRecordActionGuardTestBundle.ActionKey);

                var denied = await execute.Should().ThrowAsync<RuntimeActionPermissionException>();
                denied.Which.PermissionAction.Should().Be(permissionAction);
                denied.Which.Target.Should().Be(target);
                denied.Which.Message.Should().NotContain(Id(approved).ToString(),
                    "permission failure must not enumerate the hidden related identity");
                (await ReadBatchAsync(database, deniedBatch))["status"].Should().Be("draft");
            }

            var hiddenDraft = () => database.Actions.ExecuteActionAsync(
                database.Tenant,
                User(-91_004, "scoped_operator"),
                "batch",
                Id(draftBatch),
                RelatedRecordActionGuardTestBundle.ActionKey);
            var hiddenDraftDenied = await hiddenDraft.Should().ThrowAsync<RuntimeActionPermissionException>();
            hiddenDraftDenied.Which.PermissionAction.Should().Be("read");
            hiddenDraftDenied.Which.Target.Should().Be("formula_revision");
            hiddenDraftDenied.Which.Message.Should().NotContain(Id(draft).ToString());
            (await ReadBatchAsync(database, draftBatch))["status"].Should().Be("draft",
                "hidden Draft and Approved selected revisions must produce the same non-enumerating denial");

            var compiledAction = new ErpBundleCompiler()
                .Compile(RelatedRecordActionGuardTestBundle.Create())
                .Actions.Single();
            var tamperedBatch = await CreateBatchAsync(database, owner, approved, "Tampered metadata batch");
            var identityFilter = compiledAction.Steps
                .Single(step => step.Id == RelatedRecordActionGuardTestBundle.QueryStepId)
                .Where[0];
            foreach (var (tamperedIdentity, expectedMessage) in new[]
                     {
                         (
                             identityFilter with { Op = "gte" },
                             "*system id filter*exact operator 'eq'*"),
                         (
                             identityFilter with
                             {
                                 ValueFrom = new ActionValueFromDefinition(
                                     "name",
                                     null,
                                     null,
                                     null,
                                     null)
                             },
                             "*system id filter source field 'name' does not identify entity 'formula_revision'*"),
                         (
                             identityFilter with { Value = 1L, ValueFrom = null },
                             "*system id filter*relation-bound value_from.record identity*literal ids are forbidden*")
                     })
            {
                var tamperedSteps = compiledAction.Steps.Select(step =>
                    step.Id == RelatedRecordActionGuardTestBundle.QueryStepId
                        ? step with { Where = [tamperedIdentity, step.Where[1]] }
                        : step).ToArray();
                await PersistActionAsync(database, compiledAction with { Steps = tamperedSteps });
                (await database.Actions.ListRecordActionsAsync(
                        database.Tenant,
                        @operator,
                        "batch",
                        Id(tamperedBatch)))
                    .Should().NotContain(action =>
                        action.Key == RelatedRecordActionGuardTestBundle.ActionKey,
                        "unsafe persisted identity metadata must fail closed at discovery");

                var tampered = () => database.Actions.ExecuteActionAsync(
                    database.Tenant,
                    @operator,
                    "batch",
                    Id(tamperedBatch),
                    RelatedRecordActionGuardTestBundle.ActionKey);
                await tampered.Should().ThrowAsync<RuntimeActionValidationException>()
                    .WithMessage(expectedMessage);
            }

            (await ReadBatchAsync(database, tamperedBatch))["status"].Should().Be("draft");
        });
    }

    [PostgresFact]
    public async Task Guard_query_holds_exact_related_row_for_share_until_source_mutation_commits()
    {
        await WithDatabaseAsync("related_guard_lock", async database =>
        {
            var owner = SystemOwner();
            var approved = await database.Records.CreateRecordAsync(
                database.Tenant,
                owner,
                "formula_revision",
                RuntimeJson.JsonObject(
                    ("name", "Concurrency formula"),
                    ("status", "approved"),
                    ("owner_principal", owner.UserId)));
            var batch = await CreateBatchAsync(database, owner, approved, "Concurrency batch");
            await InstallMutationPauseTriggerAsync(database);

            await using var blocker = new NpgsqlConnection(
                database.Factory.CreateConnectionString(database.Tenant));
            await blocker.OpenAsync();
            await using (var acquire = blocker.CreateCommand())
            {
                acquire.CommandText = "select pg_advisory_lock(@key)";
                acquire.Parameters.AddWithValue("key", MutationPauseAdvisoryKey);
                await acquire.ExecuteScalarAsync();
            }

            var actionTask = database.Actions.ExecuteActionAsync(
                database.Tenant,
                User(-91_101, "operator"),
                "batch",
                Id(batch),
                RelatedRecordActionGuardTestBundle.ActionKey);
            var relatedUpdateBlocked = false;
            try
            {
                await WaitForPausedBatchMutationAsync(database);
                relatedUpdateBlocked = await RelatedRevisionUpdateBlocksAsync(database, Id(approved));
                await ReleaseMutationPauseAsync(blocker);

                var result = await actionTask.WaitAsync(TimeSpan.FromSeconds(10));
                result.Success.Should().BeTrue();
            }
            finally
            {
                await ReleaseMutationPauseAsync(blocker);
                if (!actionTask.IsCompleted)
                {
                    await actionTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            relatedUpdateBlocked.Should().BeTrue(
                "the exact approved formula row must remain FOR SHARE locked through the batch mutation commit");
            (await ReadBatchAsync(database, batch))["status"].Should().Be("released");
            (await database.Records.GetRecordAsync(
                database.Tenant,
                "formula_revision",
                Id(approved)))!["status"].Should().Be("approved");
        });
    }

    private static async Task WithDatabaseAsync(
        string prefix,
        Func<GuardDatabase, Task> test)
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_{prefix}_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create(
                prefix,
                $"{prefix}.erpgenerator.test",
                databaseName,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var compiled = new ErpBundleCompiler().Compile(RelatedRecordActionGuardTestBundle.Create());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var permissionService = new RuntimePermissionService(factory);
            var database = new GuardDatabase(
                tenant,
                factory,
                new RuntimeRecordStore(factory),
                new RuntimeActionStore(factory, permissionService));

            await test(database);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static async Task<Dictionary<string, object?>> CreateBatchAsync(
        GuardDatabase database,
        RuntimeUserContext owner,
        IReadOnlyDictionary<string, object?> revision,
        string name) =>
        await database.Records.CreateRecordAsync(
            database.Tenant,
            owner,
            "batch",
            RuntimeJson.JsonObject(
                ("name", name),
                ("formula_revision_id", Id(revision)),
                ("status", "draft")));

    private static async Task<Dictionary<string, object?>> ReadBatchAsync(
        GuardDatabase database,
        IReadOnlyDictionary<string, object?> batch) =>
        (await database.Records.GetRecordAsync(database.Tenant, "batch", Id(batch)))!;

    private static RuntimeUserContext User(long id, string role) =>
        new(id, $"{role}@related-guard.test", role) { Roles = [role] };

    private static RuntimeUserContext SystemOwner() =>
        new(1, "owner@related-guard.test", RuntimeSystemRoleIds.TenantOwner)
        {
            Roles = [RuntimeSystemRoleIds.TenantOwner]
        };

    private static long Id(IReadOnlyDictionary<string, object?> record) =>
        Convert.ToInt64(record["id"]);

    private static async Task PersistActionAsync(
        GuardDatabase database,
        ActionDefinition action)
    {
        await using var connection = new NpgsqlConnection(
            database.Factory.CreateConnectionString(database.Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.action_definitions
            set metadata = cast(@metadata as jsonb)
            where id = @actionKey
            """;
        command.Parameters.AddWithValue(
            "metadata",
            JsonSerializer.Serialize(action, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.Parameters.AddWithValue("actionKey", action.Key);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task InstallMutationPauseTriggerAsync(GuardDatabase database)
    {
        await using var connection = new NpgsqlConnection(
            database.Factory.CreateConnectionString(database.Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            create or replace function app.pause_related_guard_batch_update()
            returns trigger
            language plpgsql
            as $pause$
            begin
              perform pg_advisory_xact_lock({MutationPauseAdvisoryKey});
              return new;
            end
            $pause$;

            create trigger pause_related_guard_batch_update
            before update on app.batches
            for each row execute function app.pause_related_guard_batch_update()
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForPausedBatchMutationAsync(GuardDatabase database)
    {
        await using var connection = new NpgsqlConnection(
            database.Factory.CreateConnectionString(database.Tenant));
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 150; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select exists (
                  select 1
                  from pg_stat_activity
                  where datname = current_database()
                    and wait_event_type = 'Lock'
                    and wait_event = 'advisory'
                    and query ilike '%update "app"."batches"%'
                )
                """;
            if (await command.ExecuteScalarAsync() is true)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Action did not reach the paused batch mutation.");
    }

    private static async Task<bool> RelatedRevisionUpdateBlocksAsync(
        GuardDatabase database,
        long revisionId)
    {
        await using var connection = new NpgsqlConnection(
            database.Factory.CreateConnectionString(database.Tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update app.formula_revisions
            set status = 'revoked'
            where id = @id
            """;
        command.Parameters.AddWithValue("id", revisionId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
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

    private static async Task ReleaseMutationPauseAsync(NpgsqlConnection blocker)
    {
        if (blocker.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var command = blocker.CreateCommand();
        command.CommandText = "select pg_advisory_unlock(@key)";
        command.Parameters.AddWithValue("key", MutationPauseAdvisoryKey);
        await command.ExecuteScalarAsync();
    }

    private sealed record GuardDatabase(
        ResolvedTenantEnvironment Tenant,
        TenantDatabaseConnectionFactory Factory,
        RuntimeRecordStore Records,
        RuntimeActionStore Actions);
}
