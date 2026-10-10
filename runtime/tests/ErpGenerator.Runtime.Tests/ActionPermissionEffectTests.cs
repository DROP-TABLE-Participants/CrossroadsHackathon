using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class ActionPermissionEffectTests
{
    [Fact]
    public void Analyzer_derives_read_create_update_many_and_update_current_effects()
    {
        var analysis = ActionPermissionEffectAnalyzer.Analyze(
            "sales_order",
            [
                new("find_lines", "query_records", "sales_order_line"),
                new("create_shipment", "create_record", "shipment"),
                new("create_shipment_lines", "create_records_for_each", "shipment_line", ForEachResult: "find_lines"),
                new("mark_lines", "update_records", RecordsFromResult: "find_lines"),
                new("mark_order", "update_current_record")
            ]);

        analysis.Errors.Should().BeEmpty();
        analysis.Requirements.Should().BeEquivalentTo(
        [
            new ActionPermissionEffectRequirement("find_lines", "read", "sales_order_line"),
            new ActionPermissionEffectRequirement("create_shipment", "create", "shipment"),
            new ActionPermissionEffectRequirement("create_shipment_lines", "create", "shipment_line"),
            new ActionPermissionEffectRequirement("mark_lines", "update", "sales_order_line"),
            new ActionPermissionEffectRequirement("mark_order", "update", "sales_order")
        ]);
    }

    [Fact]
    public void Analyzer_fails_closed_when_mutation_query_result_cannot_be_resolved()
    {
        var analysis = ActionPermissionEffectAnalyzer.Analyze(
            "customer",
            [new("update_missing", "update_records", RecordsFromResult: "missing_query")]);

        analysis.IsValid.Should().BeFalse();
        analysis.Errors.Should().ContainSingle(error =>
            error.Contains("unknown query result 'missing_query'", StringComparison.OrdinalIgnoreCase));
        analysis.Requirements.Should().BeEmpty();
    }

    [Theory]
    [InlineData("read", "customer", "find_customers")]
    [InlineData("create", "invoice", "create_invoice")]
    [InlineData("update", "customer", "update_customers")]
    public void Compiler_rejects_action_when_declared_permissions_do_not_cover_effect(
        string missingAction,
        string missingEntity,
        string expectedStep)
    {
        var bundle = EffectBundle();
        var action = bundle.Actions.Single();
        action.Requires.Permissions.RemoveAll(permission =>
            permission.Action.Equals(missingAction, StringComparison.OrdinalIgnoreCase) &&
            permission.Entity.Equals(missingEntity, StringComparison.OrdinalIgnoreCase));

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains($"step '{expectedStep}'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains($"permission '{missingAction}'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains($"entity '{missingEntity}'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_wildcards_in_action_permission_requirements()
    {
        var bundle = EffectBundle();
        bundle.Actions.Single().Requires.Permissions.Clear();
        bundle.Actions.Single().Requires.Permissions.Add(new ErpGrant { Action = "*", Entity = "customer" });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("concrete action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_ambiguous_direct_and_effect_mutation_modes()
    {
        var bundle = EffectBundle();
        bundle.Actions.Single().Requires.Permissions.Add(
            new ErpGrant { Action = "effect_update", Entity = "customer" });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("ambiguously declares", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("update", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("customer", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("effect_create")]
    public void Compiler_rejects_orphan_direct_or_effect_permission_without_matching_mutation(
        string mutationAction)
    {
        var bundle = EffectBundle();
        bundle.Actions.Single().Requires.Permissions.Add(
            new ErpGrant { Action = mutationAction, Entity = "customer" });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("without one exact matching mutation effect", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("effect_create")]
    [InlineData("effect_update")]
    public void Compiler_rejects_wildcard_entity_effect_role_grants(string effectAction)
    {
        var bundle = EffectBundle();
        bundle.Roles["effect_operator"] = new ErpRole
        {
            DisplayName = "Effect operator",
            Grants = [new ErpGrant { Action = effectAction, Entity = "*" }]
        };

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("wildcard effect authority is forbidden", StringComparison.OrdinalIgnoreCase));
    }

    private static ErpBundle EffectBundle() => new()
    {
        Id = "permission_effect_bundle",
        Name = "Permission Effect Bundle",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "crm",
                Name = "CRM",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "customer",
                        Name = "customers",
                        DisplayName = "Customers",
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                            new ErpField { Id = "processed", Name = "processed", DisplayName = "Processed", Type = "boolean" }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "invoice",
                        Name = "invoices",
                        DisplayName = "Invoices",
                        Fields =
                        [
                            new ErpField { Id = "customer", Name = "customer_id", DisplayName = "Customer", Type = "relation", Required = true, References = "customer" },
                            new ErpField { Id = "amount", Name = "amount", DisplayName = "Amount", Type = "money", Required = true }
                        ]
                    }
                ]
            }
        ],
        Actions =
        [
            new ErpAction
            {
                Key = "create_invoice_and_mark",
                Label = "Create invoice and mark",
                Entity = "customer",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Action = "read", Entity = "customer" },
                        new ErpGrant { Action = "create", Entity = "invoice" },
                        new ErpGrant { Action = "update", Entity = "customer" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep { Id = "find_customers", Type = "query_records", Entity = "customer" },
                    new ErpActionStep
                    {
                        Id = "create_invoice",
                        Type = "create_record",
                        Entity = "invoice",
                        Values = new Dictionary<string, ErpActionValue>
                        {
                            ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                            ["amount"] = new() { Value = 10 }
                        }
                    },
                    new ErpActionStep
                    {
                        Id = "update_customers",
                        Type = "update_records",
                        RecordsFrom = new ErpActionRecordsFrom { Result = "find_customers" },
                        Set = new Dictionary<string, ErpActionValue> { ["processed"] = new() { Value = true } }
                    },
                    new ErpActionStep
                    {
                        Id = "update_current",
                        Type = "update_current_record",
                        Set = new Dictionary<string, ErpActionValue> { ["processed"] = new() { Value = true } }
                    }
                ]
            }
        ]
    };
}
