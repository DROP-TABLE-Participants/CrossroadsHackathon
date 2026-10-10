using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimePermissionSnapshotIntegrationTests
{
    [PostgresFact]
    public async Task Persisted_role_grant_arrays_are_admitted_atomically_only_after_catalog_validation()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_permission_snapshot_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create("permission-snapshot", "permission-snapshot.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, new ErpBundleCompiler().Compile(Bundle()));
            var permissions = new RuntimePermissionService(factory);
            var member = new RuntimeUserContext(-91_001, "synthetic-member@example.test", "member")
            {
                Roles = ["member"]
            };
            var entity = new RuntimeEntityDefinition(
                "work_items",
                "work_item",
                "work_items",
                "Work item",
                "app",
                "work_items",
                "title",
                null,
                []);

            var malformedRoleDocuments = new[]
            {
                """{}""",
                WithValidEntityGrants("""{"Action":"execute","Entity":"work_item"}"""),
                WithValidEntityGrants("""{"Action":"read","Entity":"work_item","Scope":"unknown"}"""),
                WithValidEntityGrants("""{"Action":"read","Entity":"missing_entity"}"""),
                WithValidEntityGrants("""{"Action":"read","Entity":"work_item","Unexpected":"value"}"""),
                """[{"Action":"read","Entity":"work_item"},{"Action":"read","Entity":"work_item"}]""",
                WithValidEntityGrants("""{"Action":"read","Entity":"work_item","Scope":"field","Target":"missing_field"}"""),
                WithValidEntityGrants("""{"Action":"read","Entity":"work_item","Scope":"field","Target":""}"""),
                WithValidEntityGrants("""{"Action":"*","Entity":"work_item","Scope":"field","Target":"title"}"""),
                WithValidEntityGrants("""{"Action":"read","Entity":"work_item","Scope":"entity","Target":"title"}"""),
                WithValidEntityGrants("""{"Action":"read","Entity":"*","Scope":"record","Target":"","RecordScope":{"Field":"owner_principal","Source":"actor_principal"}}"""),
                WithValidEntityGrants("""{"Action":"read","Entity":"work_item","Scope":"record","Target":"","RecordScope":{"Field":"title","Source":"actor_principal"}}"""),
                WithValidEntityGrants("""{"Action":"view","Entity":"work_item","Scope":"page","Target":"dashboard"}"""),
                WithValidEntityGrants("""{"Action":"execute","Entity":"work_item","Scope":"action","Target":"missing_action"}""")
            };

            await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await connection.OpenAsync();
            foreach (var roleDocument in malformedRoleDocuments)
            {
                await WriteRoleGrantsAsync(connection, "member", roleDocument);
                var snapshot = await permissions.CreateSnapshotAsync(connection, member);
                snapshot.Grants.Should().BeEmpty(
                    "one malformed, unknown, duplicate, or semantically invalid grant invalidates its entire persisted role array");
                permissions.IsAllowed(snapshot, "read", entity).Should().BeFalse();
                permissions.IsAllowed(snapshot, "update", entity).Should().BeFalse();
            }

            await WriteRoleGrantsAsync(
                connection,
                "member",
                """
                [
                  {"Action":"read","Entity":"work_item"},
                  {"Action":"update","Entity":"work_item"},
                  {"Action":"read","Entity":"work_item","Scope":"field","Target":"title"},
                  {"Action":"read","Entity":"work_item","Scope":"field","Target":"warehouse"},
                  {"Action":"read","Entity":"work_item","Scope":"record","Target":"","RecordScope":{"Field":"owner_principal","Source":"actor_principal"}},
                  {"Action":"read","Entity":"work_item","Scope":"record","Target":"","RecordScope":{"Field":"warehouse","Source":"linked_record","LinkedEntity":"warehouse"}}
                ]
                """);
            var validSnapshot = await permissions.CreateSnapshotAsync(connection, member);
            validSnapshot.Grants.Should().HaveCount(6);
            validSnapshot.Grants.Should().Contain(grant =>
                grant.Scope == PermissionGrantScopes.Field && grant.Target == "title");
            validSnapshot.Grants.Should().Contain(grant =>
                grant.Scope == PermissionGrantScopes.Field && grant.Target == "warehouse_id",
                "field-id aliases must canonicalize to the runtime field name used by permission checks");
            validSnapshot.Grants.Should().Contain(grant =>
                grant.Scope == PermissionGrantScopes.Record &&
                grant.RecordScope!.Source == RecordPermissionScopeSources.ActorPrincipal);
            validSnapshot.Grants.Should().Contain(grant =>
                grant.Scope == PermissionGrantScopes.Record &&
                grant.RecordScope!.Source == RecordPermissionScopeSources.LinkedRecord);

            await WriteRoleGrantsAsync(
                connection,
                "member",
                WithValidEntityGrants(
                    """{"Action":"read","Entity":"work_item","Scope":"field","Target":"missing_field"}"""));
            var memberWithIndependentValidRole = member with { Roles = ["member", "valid_reader"] };
            var isolatedSnapshot = await permissions.CreateSnapshotAsync(connection, memberWithIndependentValidRole);
            isolatedSnapshot.Grants.Should().ContainSingle();
            isolatedSnapshot.Grants[0].Should().Match<RuntimePermissionGrant>(grant =>
                grant.Scope == PermissionGrantScopes.Entity &&
                grant.Action == "read" &&
                grant.Entity == "work_item");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Effect_transaction_snapshot_serializes_user_role_and_grant_revocation()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_permission_lock_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create("permission-lock", "permission-lock.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, new ErpBundleCompiler().Compile(Bundle()));
            var users = new RuntimeUserStore(factory);
            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"owner-{suffix}@example.test");
            var member = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(
                users,
                runtimeDatabase,
                tenant,
                owner,
                $"member-{suffix}@example.test");
            var permissions = new RuntimePermissionService(factory);

            var snapshotReady = new TaskCompletionSource<RuntimePermissionSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseEffect = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var holdingEffect = Task.Run(async () =>
            {
                try
                {
                    await using var effectConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
                    await effectConnection.OpenAsync();
                    await using var effectTransaction = await effectConnection.BeginTransactionAsync();
                    var snapshot = await permissions.CreateSnapshotAsync(
                        effectConnection,
                        effectTransaction,
                        member);
                    snapshotReady.TrySetResult(snapshot);
                    await releaseEffect.Task;
                    await effectTransaction.CommitAsync();
                }
                catch (Exception exception)
                {
                    snapshotReady.TrySetException(exception);
                    throw;
                }
            });

            try
            {
                var snapshot = await snapshotReady.Task;
                snapshot.Grants.Should().NotBeEmpty();

                await AssertLockTimeoutAsync(
                    factory.CreateConnectionString(tenant),
                    "update _runtime.runtime_users set status = 'inactive' where id = @value",
                    member.UserId);
                await AssertLockTimeoutAsync(
                    factory.CreateConnectionString(tenant),
                    "delete from _runtime.runtime_user_roles where user_id = @value and role_id = 'member'",
                    member.UserId);
                await AssertLockTimeoutAsync(
                    factory.CreateConnectionString(tenant),
                    "update _runtime.role_definitions set updated_at = now() where id = @value",
                    "member");
            }
            finally
            {
                releaseEffect.TrySetResult();
                await holdingEffect;
            }

            await using (var revokeConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant)))
            {
                await revokeConnection.OpenAsync();
                await using var revoke = revokeConnection.CreateCommand();
                revoke.CommandText = "delete from _runtime.runtime_user_roles where user_id = @userId and role_id = 'member'";
                revoke.Parameters.AddWithValue("userId", member.UserId);
                (await revoke.ExecuteNonQueryAsync()).Should().Be(1,
                    "revocation must proceed immediately after the effect transaction releases its snapshot locks");
            }

            await using var verificationConnection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await verificationConnection.OpenAsync();
            var revokedSnapshot = await permissions.CreateSnapshotAsync(verificationConnection, member);
            revokedSnapshot.Grants.Should().BeEmpty();
            revokedSnapshot.HasSystemOwnerAuthority.Should().BeFalse();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static string WithValidEntityGrants(string lastGrant) =>
        $"[{{\"Action\":\"read\",\"Entity\":\"work_item\"}},{{\"Action\":\"update\",\"Entity\":\"work_item\"}},{lastGrant}]";

    private static async Task WriteRoleGrantsAsync(NpgsqlConnection connection, string roleId, string grantsJson)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.role_definitions
            set grants = cast(@grants as jsonb), updated_at = now()
            where id = @roleId
            """;
        command.Parameters.AddWithValue("grants", grantsJson);
        command.Parameters.AddWithValue("roleId", roleId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task AssertLockTimeoutAsync(string connectionString, string sql, object value)
    {
        var execute = async () =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var timeout = connection.CreateCommand())
            {
                timeout.CommandText = "set lock_timeout = '300ms'";
                await timeout.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("value", value);
            await command.ExecuteNonQueryAsync();
        };

        var exception = await execute.Should().ThrowAsync<PostgresException>();
        exception.Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
    }

    private static ErpBundle Bundle() => new()
    {
        Id = "permission_snapshot",
        Name = "Permission snapshot",
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
                        Id = "warehouse",
                        Name = "warehouses",
                        DisplayName = "Warehouse",
                        Display = new ErpEntityDisplay { Primary = "name" },
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name",
                                Name = "name",
                                DisplayName = "Name",
                                Type = "string",
                                Required = true
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "work_item",
                        Name = "work_items",
                        DisplayName = "Work item",
                        Display = new ErpEntityDisplay { Primary = "title" },
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "title",
                                Name = "title",
                                DisplayName = "Title",
                                Type = "string",
                                Required = true
                            },
                            new ErpField
                            {
                                Id = "owner_principal",
                                Name = "owner_principal",
                                DisplayName = "Owner principal",
                                Type = "integer"
                            },
                            new ErpField
                            {
                                Id = "warehouse",
                                Name = "warehouse_id",
                                DisplayName = "Warehouse",
                                Type = "relation",
                                References = "warehouse"
                            }
                        ]
                    }
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
                    EntityGrant("read"),
                    EntityGrant("update"),
                    new ErpGrant
                    {
                        Scope = PermissionGrantScopes.Field,
                        Action = "read",
                        Entity = "work_item",
                        Target = "title"
                    }
                ]
            },
            ["valid_reader"] = new()
            {
                DisplayName = "Valid reader",
                Grants = [EntityGrant("read")]
            }
        }
    };

    private static ErpGrant EntityGrant(string action) => new()
    {
        Action = action,
        Entity = "work_item"
    };
}
