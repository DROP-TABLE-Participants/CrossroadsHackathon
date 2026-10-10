using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Services.ArtificialIntelligence;
using ErpGenerator.Runtime.Services.Assistant;
using ErpGenerator.Runtime.Services.Runtime;
using ErpGenerator.Runtime.Shared.ArtificialIntelligence;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeToolCatalogIntegrationTests
{
    [PostgresFact]
    public async Task Catalog_omits_unauthorized_and_disabled_tools()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_ai_tools_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create("ai-tools", "ai-tools.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder()).ApplyCompiledBundleAsync(
                tenant,
                new ErpBundleCompiler().Compile(AiNativeRuntimeContractTests.Bundle()));
            var catalog = new RuntimeToolCatalogService(
                new RuntimeActiveBundleReader(factory),
                new RuntimeRecordStore(factory),
                new RuntimePermissionService(factory));
            var viewer = new RuntimeUserContext(-501, "viewer@example.test", "viewer")
            {
                Roles = ["viewer"]
            };

            var visible = await catalog.ListAuthorizedAsync(tenant, viewer);

            visible.Should().Contain(tool => tool.Key == "records.list:sales_order");
            visible.Should().Contain(tool => tool.Key == "records.get:sales_order");
            visible.Should().Contain(tool => tool.Key == "reports.execute:open_order_count");
            visible.Should().NotContain(tool => tool.Key.Contains("sales_order_line", StringComparison.Ordinal));
            visible.Should().NotContain(tool => tool.Key == "audit.get");
            visible.Should().OnlyContain(tool =>
                tool.Enabled &&
                tool.Mode == RuntimeToolModes.Read &&
                tool.BundleHash.Length == 64);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Owner_can_inspect_prepare_contracts_but_they_remain_non_executable()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_ai_preview_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create("ai-preview", "ai-preview.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder()).ApplyCompiledBundleAsync(
                tenant,
                new ErpBundleCompiler().Compile(AiNativeRuntimeContractTests.Bundle()));
            var catalog = new RuntimeToolCatalogService(
                new RuntimeActiveBundleReader(factory),
                new RuntimeRecordStore(factory),
                new RuntimePermissionService(factory));
            var owner = new RuntimeUserContext(-502, "owner@example.test", "owner")
            {
                Roles = ["owner"]
            };

            var visible = await catalog.ListAuthorizedAsync(
                tenant,
                owner,
                includeDisabled: true);

            visible.Should().Contain(tool => tool.Key == "audit.get");
            visible.Where(tool => tool.Mode == RuntimeToolModes.Prepare)
                .Should().NotBeEmpty()
                .And.OnlyContain(tool => !tool.Enabled);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Assistant_interaction_ledger_persists_grounded_completion()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_ai_ledger_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create("ai-ledger", "ai-ledger.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var compiled = new ErpBundleCompiler().Compile(AiNativeRuntimeContractTests.Bundle());
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder()).ApplyCompiledBundleAsync(tenant, compiled);
            var store = new RuntimeAssistantInteractionStore(factory);
            var interactionId = Guid.NewGuid();
            var context = new RuntimeAssistantContext(
                RuntimeAssistantContextKinds.RecordDetail,
                "/entities/sales_order/42",
                "sales_order",
                42)
            {
                BundleHash = compiled.BundleHash
            };
            var evidence = new RuntimeEvidenceReference(
                "evidence_order_42",
                RuntimeEvidenceKinds.Record,
                "Sales order #42",
                "/entities/sales_order/42",
                DateTimeOffset.UtcNow,
                compiled.BundleHash,
                new string('a', 64))
            {
                EntityKey = "sales_order",
                RecordId = 42
            };

            await store.StartAsync(
                tenant,
                new RuntimeUserContext(17, "operator@example.test", "operator"),
                interactionId,
                context,
                "What is blocking this order?",
                "test-model",
                "assistant:test",
                CancellationToken.None);
            await store.CompleteAsync(
                tenant,
                interactionId,
                "The order is waiting for approval.",
                [evidence],
                CancellationToken.None);

            await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select status, bundle_hash, question, answer, evidence -> 0 ->> 'id'
                from _runtime.assistant_interactions
                where id = @id
                """;
            command.Parameters.AddWithValue("id", interactionId);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("completed");
            reader.GetString(1).Should().Be(compiled.BundleHash);
            reader.GetString(2).Should().Be("What is blocking this order?");
            reader.GetString(3).Should().Be("The order is waiting for approval.");
            reader.GetString(4).Should().Be(evidence.Id);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }
}
