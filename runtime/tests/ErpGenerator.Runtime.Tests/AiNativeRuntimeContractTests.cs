using FluentAssertions;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Shared.ArtificialIntelligence;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class AiNativeRuntimeContractTests
{
    [Fact]
    public void Compiler_emits_relationship_line_item_and_exception_metadata()
    {
        var compiled = new ErpBundleCompiler().Compile(Bundle());

        var lineItem = compiled.Entities.Single(entity => entity.Id == "sales_order_line");
        lineItem.Relationships.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new RelationshipDefinition(
                "sales_order_lines_order",
                "sales_order_line",
                "sales_order",
                "sales_order",
                true,
                "many_to_one",
                "lines",
                "restrict"));
        compiled.Entities.Single(entity => entity.Id == "sales_order")
            .Collections.Should().ContainSingle().Which.Should().Match<EntityCollectionDefinition>(collection =>
                collection.Kind == "line_items" &&
                collection.ChildEntity == "sales_order_line" &&
                collection.ParentRelation == "sales_order" &&
                collection.OrderField == "line_number");
        compiled.ExceptionRules.Should().ContainSingle().Which.Should().Match<ExceptionRuleDefinition>(rule =>
            rule.Report == "open_order_count" &&
            rule.Metric == "count" &&
            rule.Comparator == "gt" &&
            rule.Severity == "warning");
    }

    [Fact]
    public void Relation_tables_use_indexes_and_restrictive_foreign_keys_after_table_creation()
    {
        var compiled = new ErpBundleCompiler().Compile(Bundle());
        var lineItems = compiled.Tables.Single(table => table.TableName == "sales_order_lines");
        lineItems.Indexes.Should().Contain(index =>
            index.Columns.Count == 1 &&
            index.Columns[0] == "sales_order_id" &&
            !index.IsUnique);
        lineItems.ForeignKeys.Should().ContainSingle().Which.Should().Match<ForeignKeyPlan>(foreignKey =>
            foreignKey.Column == "sales_order_id" &&
            foreignKey.ReferencedTable == "sales_orders" &&
            foreignKey.OnDelete == "restrict");

        var statements = new RuntimeSqlBuilder().BuildDeploymentStatements(compiled).ToArray();
        var lastTable = Array.FindLastIndex(statements, statement =>
            statement.StartsWith("create table if not exists \"app\".", StringComparison.Ordinal));
        var firstForeignKey = Array.FindIndex(statements, statement =>
            statement.Contains("foreign key", StringComparison.OrdinalIgnoreCase));
        firstForeignKey.Should().BeGreaterThan(lastTable);
        statements[firstForeignKey].Should().Contain("on delete restrict");
    }

    [Fact]
    public void Tool_catalog_is_bundle_bound_and_keeps_all_prepare_tools_disabled()
    {
        var compiled = new ErpBundleCompiler().Compile(Bundle());

        var catalog = RuntimeToolCatalogBuilder.Build(compiled);

        catalog.Should().OnlyContain(tool => tool.BundleHash == compiled.BundleHash);
        catalog.Where(tool => tool.Mode == RuntimeToolModes.Read)
            .Should().OnlyContain(tool => tool.Enabled && tool.Risk == RuntimeToolRisks.ReadOnly);
        catalog.Where(tool => tool.Mode == RuntimeToolModes.Prepare)
            .Should().NotBeEmpty()
            .And.OnlyContain(tool => !tool.Enabled && tool.Risk == RuntimeToolRisks.Consequential);
        catalog.Should().Contain(tool => tool.Key == "reports.execute:open_order_count");
        catalog.Should().Contain(tool => tool.Key == "records.get:sales_order");
        catalog.Should().NotContain(tool => tool.Key.Contains("execute_change", StringComparison.Ordinal));
    }

    [Fact]
    public void Preview_contract_is_non_executable_and_bound_to_security_snapshots()
    {
        var preview = new RuntimeActionPreview(
            Guid.NewGuid(),
            "records.prepare_update:sales_order",
            RuntimeToolRisks.Consequential,
            "entity",
            "sales_order",
            [new RuntimeActionPreviewChange("status", "draft", "confirmed")],
            [new RuntimeToolEffect("effect_update", "sales_order", "status")],
            ["Confirmation is required."],
            [],
            "bundle-hash",
            DateTimeOffset.UtcNow.AddMinutes(5))
        {
            IdempotencyKey = "one-use",
            PermissionSnapshotHash = "permission-hash",
            RecordVersion = "record-version"
        };

        preview.ExecutionEnabled.Should().BeFalse();
        var confirmation = new RuntimeActionConfirmation(
            preview.Id,
            preview.IdempotencyKey,
            preview.PermissionSnapshotHash,
            preview.BundleHash,
            preview.RecordVersion);
        confirmation.Should().NotBeNull();
    }

    [Theory]
    [InlineData("cascade", "archive_behavior must be exactly 'restrict'")]
    [InlineData("set_null", "archive_behavior must be exactly 'restrict'")]
    public void Compiler_rejects_non_restrictive_relationship_deletion(
        string archiveBehavior,
        string expected)
    {
        var bundle = Bundle();
        var fields = bundle.Modules[0].Entities
            .Single(entity => entity.Id == "sales_order_line")
            .Fields;
        fields[0] = RelationField(required: true, archiveBehavior);

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_rejects_line_items_without_a_required_parent_relation()
    {
        var bundle = Bundle();
        var fields = bundle.Modules[0].Entities
            .Single(entity => entity.Id == "sales_order_line")
            .Fields;
        fields[0] = RelationField(required: false, "restrict");

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("parent_relation must be a required relation", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_rejects_exception_rules_that_do_not_resolve_to_a_report_metric()
    {
        var bundle = Bundle();
        bundle.ExceptionRules[0] = ExceptionRule("invented_metric");

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("references unknown metric", StringComparison.Ordinal));
    }

    internal static ErpBundle Bundle() => new()
    {
        Id = "ai_native_contracts",
        Name = "AI native contracts",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "sales",
                Name = "Sales",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "sales_order",
                        Name = "sales_orders",
                        DisplayName = "Sales orders",
                        Display = new ErpEntityDisplay { Primary = "number" },
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "number",
                                Name = "number",
                                DisplayName = "Number",
                                Type = "string",
                                Required = true,
                                Unique = true
                            }
                        ],
                        Collections =
                        [
                            new ErpEntityCollection
                            {
                                Key = "lines",
                                Kind = "line_items",
                                Label = "Lines",
                                ChildEntity = "sales_order_line",
                                ParentRelation = "sales_order",
                                OrderField = "line_number",
                                ShownFields = ["line_number", "description", "quantity"],
                                EditableFields = ["description", "quantity"]
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "sales_order_line",
                        Name = "sales_order_lines",
                        DisplayName = "Sales order lines",
                        Display = new ErpEntityDisplay { Primary = "description" },
                        Fields =
                        [
                            RelationField(required: true, "restrict"),
                            new ErpField
                            {
                                Id = "line_number",
                                Name = "line_number",
                                DisplayName = "Line",
                                Type = "integer",
                                Required = true
                            },
                            new ErpField
                            {
                                Id = "description",
                                Name = "description",
                                DisplayName = "Description",
                                Type = "string",
                                Required = true
                            },
                            new ErpField
                            {
                                Id = "quantity",
                                Name = "quantity",
                                DisplayName = "Quantity",
                                Type = "decimal",
                                Required = true
                            }
                        ]
                    }
                ]
            }
        ],
        Reports =
        [
            new ErpReport
            {
                Key = "open_order_count",
                Label = "Open order count",
                Source = "sales_order",
                Metrics =
                [
                    new ErpReportMetric
                    {
                        Key = "count",
                        Label = "Count",
                        Op = "count"
                    }
                ]
            }
        ],
        ExceptionRules =
        [
            ExceptionRule("count")
        ],
        Roles = new Dictionary<string, ErpRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = new()
            {
                DisplayName = "Owner",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            },
            ["viewer"] = new()
            {
                DisplayName = "Viewer",
                Grants = [new ErpGrant { Action = "read", Entity = "sales_order" }]
            }
        }
    };

    private static ErpField RelationField(bool required, string archiveBehavior) => new()
    {
        Id = "sales_order",
        Name = "sales_order_id",
        DisplayName = "Sales order",
        Type = "relation",
        Required = required,
        References = "sales_order",
        Relationship = new ErpRelationship
        {
            Key = "sales_order_lines_order",
            Cardinality = "many_to_one",
            Inverse = "lines",
            ArchiveBehavior = archiveBehavior
        }
    };

    private static ErpExceptionRule ExceptionRule(string metric) => new()
    {
        Id = "open_orders",
        Label = "Open orders",
        Report = "open_order_count",
        Metric = metric,
        Comparator = "gt",
        Threshold = 20,
        Severity = "warning",
        Schedule = "0 * * * *",
        RecipientRoles = ["owner"],
        DeduplicationKey = "open_orders"
    };
}
