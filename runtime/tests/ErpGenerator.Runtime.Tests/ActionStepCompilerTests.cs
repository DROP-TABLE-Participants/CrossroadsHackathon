using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class ActionStepCompilerTests
{
    [Fact]
    public void Compiler_accepts_update_current_record_action_step()
    {
        var bundle = UpdateCurrentRecordBundle();

        var compiled = new ErpBundleCompiler().Compile(bundle);

        var step = compiled.Actions.Single().Steps.Single(candidate => candidate.Id == "mark_customer_converted");
        step.Type.Should().Be("update_current_record");
        step.Set.Keys.Should().Contain(["converted", "generated_invoice"]);
    }

    [Fact]
    public void Compiler_rejects_update_current_record_unknown_field()
    {
        var bundle = UpdateCurrentRecordBundle(set:
            new Dictionary<string, ErpActionValue>
            {
                ["missing_field"] = new() { Value = true }
            });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("unknown field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_update_current_record_computed_field()
    {
        var bundle = UpdateCurrentRecordBundle(set:
            new Dictionary<string, ErpActionValue>
            {
                ["score"] = new() { Value = 10 }
            });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("computed field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_update_current_record_unknown_step_reference()
    {
        var bundle = UpdateCurrentRecordBundle(set:
            new Dictionary<string, ErpActionValue>
            {
                ["generated_invoice"] = new()
                {
                    ValueFrom = new ErpActionValueFrom
                    {
                        Step = "missing_step",
                        Field = "id"
                    }
                }
            });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("unknown prior step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_update_current_record_sql_like_content()
    {
        var bundle = UpdateCurrentRecordBundle(set:
            new Dictionary<string, ErpActionValue>
            {
                ["name"] = new() { Value = "drop table customers" }
            });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>();
    }

    [Fact]
    public void Compiler_rejects_update_current_record_without_set_values()
    {
        var bundle = UpdateCurrentRecordBundle(set: new Dictionary<string, ErpActionValue>());

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("must declare set values", StringComparison.OrdinalIgnoreCase));
    }

    private static ErpBundle UpdateCurrentRecordBundle(Dictionary<string, ErpActionValue>? set = null)
    {
        var bundle = new ErpBundle
        {
            Id = "action_step_bundle",
            Name = "Action Step Bundle",
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
                                new ErpField { Id = "converted", Name = "converted", DisplayName = "Converted", Type = "boolean", Required = false },
                                new ErpField { Id = "generated_invoice", Name = "generated_invoice_id", DisplayName = "Generated invoice", Type = "relation", Required = false, References = "invoice" },
                                new ErpField { Id = "quantity", Name = "quantity", DisplayName = "Quantity", Type = "number", Required = false },
                                new ErpField { Id = "unit_price", Name = "unit_price", DisplayName = "Unit price", Type = "money", Required = false },
                                new ErpField
                                {
                                    Id = "score",
                                    Name = "score",
                                    DisplayName = "Score",
                                    Type = "money",
                                    Required = false,
                                    Computed = new ErpComputedField
                                    {
                                        Op = "multiply",
                                        Left = new ErpComputedOperand { Field = "quantity" },
                                        Right = new ErpComputedOperand { Field = "unit_price" }
                                    }
                                }
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
                    Label = "Create invoice and mark customer",
                    Entity = "customer",
                    Type = "record_action",
                    Requires = new ErpActionRequires
                    {
                        Permissions =
                        [
                            new ErpGrant { Entity = "customer", Action = "update" },
                            new ErpGrant { Entity = "invoice", Action = "create" }
                        ]
                    },
                    Steps =
                    [
                        new ErpActionStep
                        {
                            Id = "create_invoice",
                            Type = "create_record",
                            Entity = "invoice",
                            Values = new Dictionary<string, ErpActionValue>
                            {
                                ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                                ["amount"] = new() { Value = 100 }
                            }
                        },
                        new ErpActionStep
                        {
                            Id = "mark_customer_converted",
                            Type = "update_current_record",
                            Set = set ?? new Dictionary<string, ErpActionValue>
                            {
                                ["converted"] = new() { Value = true },
                                ["generated_invoice"] = new()
                                {
                                    ValueFrom = new ErpActionValueFrom
                                    {
                                        Step = "create_invoice",
                                        Field = "id"
                                    }
                                }
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
                    Grants = [new ErpGrant { Entity = "*", Action = "*" }]
                }
            }
        };

        return bundle;
    }
}
