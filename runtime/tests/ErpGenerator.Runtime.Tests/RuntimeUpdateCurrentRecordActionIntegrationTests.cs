using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeUpdateCurrentRecordActionIntegrationTests
{
    [PostgresFact]
    public async Task Update_current_record_action_step_updates_source_record_and_rolls_back_transactionally()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_update_current_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", database, TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            var compiled = CompileTestBundle();
            var plan = new ErpBundleMigrationPlanner().PlanMigration(null, compiled);
            var owner = new RuntimeUserContext(
                1,
                "owner@acme.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var readonlyUser = new RuntimeUserContext(2, "readonly@acme.test", "readonly") { Roles = ["readonly"] };

            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                plan,
                "erp",
                "erp",
                activate: true,
                "update current record test bundle");

            var customer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("name", $"Customer {suffix}"),
                ("quantity", 2),
                ("unit_price", 50)));

            var result = await actions.ExecuteActionAsync(
                tenant,
                owner,
                "customers",
                Convert.ToInt64(customer["id"]),
                "create_invoice_and_mark");

            result.Success.Should().BeTrue();
            result.Created.Should().NotBeNull();
            result.Created!.Entity.Should().Be("invoices");
            result.Counts["mark_customer_converted"].Should().Be(1);
            var updatedCustomer = await records.GetRecordAsync(tenant, "customers", Convert.ToInt64(customer["id"]));
            updatedCustomer.Should().NotBeNull();
            updatedCustomer!["converted"].Should().Be(true);
            updatedCustomer["generated_invoice_id"].Should().Be(result.Created.Id);

            var noReturnAction = compiled.Actions.Single(action => action.Key == "create_invoice_without_return");
            BundleAcceptanceRecordProducerContract.TryResolveActionProducedEntity(noReturnAction, out _)
                .Should().BeFalse("an action without an explicit return_result cannot be an acceptance record producer");
            var noReturnCustomer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("name", $"No return {suffix}")));
            var noReturn = await actions.ExecuteActionAsync(
                tenant,
                owner,
                "customers",
                Convert.ToInt64(noReturnCustomer["id"]),
                "create_invoice_without_return");
            noReturn.Success.Should().BeTrue();
            noReturn.Created.Should().BeNull(
                "created records are not exposed as proof identities without an exact explicit return_result");
            (await records.ListRecordsAsync(tenant, "invoices", new RuntimeRecordQuery(PageSize: 100)))
                .Records.Should().ContainSingle(invoiceRecord =>
                    Convert.ToInt64(invoiceRecord["customer_id"]) == Convert.ToInt64(noReturnCustomer["id"]),
                    "ordinary action effects still commit even when no created-record proof identity is returned");

            var unauthorizedCustomer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("name", $"Unauthorized {suffix}")));
            var unauthorized = () => actions.ExecuteActionAsync(
                tenant,
                readonlyUser,
                "customers",
                Convert.ToInt64(unauthorizedCustomer["id"]),
                "create_invoice_and_mark");
            await unauthorized.Should().ThrowAsync<RuntimeActionPermissionException>();
            var unchangedUnauthorized = await records.GetRecordAsync(tenant, "customers", Convert.ToInt64(unauthorizedCustomer["id"]));
            unchangedUnauthorized!["converted"].Should().BeNull();
            unchangedUnauthorized["generated_invoice_id"].Should().BeNull();

            var failingCustomer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("name", $"Failing {suffix}")));
            var failing = () => actions.ExecuteActionAsync(
                tenant,
                owner,
                "customers",
                Convert.ToInt64(failingCustomer["id"]),
                "mark_then_fail");
            await failing.Should().ThrowAsync<RuntimeActionValidationException>()
                .WithMessage("Amount must be numeric.");
            var afterFailure = await records.GetRecordAsync(tenant, "customers", Convert.ToInt64(failingCustomer["id"]));
            afterFailure!["converted"].Should().BeNull();
            afterFailure["generated_invoice_id"].Should().BeNull();
            (await records.ListRecordsAsync(tenant, "invoices", new RuntimeRecordQuery(PageSize: 100))).Total.Should().Be(2);

            var archivedCustomer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("name", $"Archived {suffix}")));
            await records.ArchiveRecordAsync(tenant, owner, "customers", Convert.ToInt64(archivedCustomer["id"]), "update_current_record source check");
            var archived = () => actions.ExecuteActionAsync(
                tenant,
                owner,
                "customers",
                Convert.ToInt64(archivedCustomer["id"]),
                "create_invoice_and_mark");
            await archived.Should().ThrowAsync<RuntimeActionRecordNotFoundException>();

            var audit = await records.ListAuditEventsAsync(tenant, 200);
            audit.Should().Contain(@event => @event.EventType == "record.updated");
            audit.Should().Contain(@event => @event.EventType == "action.completed");
            audit.Should().Contain(@event => @event.EventType == "action.failed");
            audit.Should().Contain(@event => @event.EventType == "permission.denied");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static ErpBundle UpdateCurrentRecordBundle()
    {
        var customer = new ErpEntity
        {
            Id = "customer",
            Name = "customers",
            DisplayName = "Customers",
            Display = new ErpEntityDisplay { Primary = "name" },
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
        };

        var invoice = new ErpEntity
        {
            Id = "invoice",
            Name = "invoices",
            DisplayName = "Invoices",
            Display = new ErpEntityDisplay { Primary = "amount" },
            Fields =
            [
                new ErpField { Id = "customer", Name = "customer_id", DisplayName = "Customer", Type = "relation", Required = true, References = "customer" },
                new ErpField { Id = "amount", Name = "amount", DisplayName = "Amount", Type = "money", Required = true }
            ]
        };

        return new ErpBundle
        {
            Id = "update_current_record_bundle",
            Name = "Current Record Action Bundle",
            Version = "1.0.0",
            Modules =
            [
                new ErpModule
                {
                    Id = "crm",
                    Name = "CRM",
                    Entities = [customer, invoice]
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
                    Requires = ActionRequirements(),
                    Steps =
                    [
                        CreateInvoiceStep("create_invoice", new ErpActionValue { Value = 100 }),
                        MarkCustomerStep(),
                        ReturnCreatedInvoiceStep("return_invoice", "create_invoice")
                    ]
                },
                new ErpAction
                {
                    Key = "create_invoice_without_return",
                    Label = "Create invoice without return",
                    Entity = "customer",
                    Type = "record_action",
                    Requires = new ErpActionRequires
                    {
                        Permissions = [new ErpGrant { Entity = "invoice", Action = "create" }]
                    },
                    Steps =
                    [
                        CreateInvoiceStep("create_invoice_without_return_record", new ErpActionValue { Value = 75 })
                    ]
                },
                new ErpAction
                {
                    Key = "mark_then_fail",
                    Label = "Mark then fail",
                    Entity = "customer",
                    Type = "record_action",
                    Requires = ActionRequirements(),
                    Steps =
                    [
                        new ErpActionStep
                        {
                            Id = "mark_customer_converted",
                            Type = "update_current_record",
                            Set = new Dictionary<string, ErpActionValue>
                            {
                                ["converted"] = new() { Value = true }
                            }
                        },
                        CreateInvoiceStep("create_bad_invoice", new ErpActionValue { ValueFrom = new ErpActionValueFrom { Record = "name" } })
                    ]
                }
            ],
            Roles = new Dictionary<string, ErpRole>
            {
                ["owner"] = new()
                {
                    DisplayName = "Owner",
                    Grants = [new ErpGrant { Entity = "*", Action = "*" }]
                },
                ["readonly"] = new()
                {
                    DisplayName = "Read only",
                    Grants = [new ErpGrant { Entity = "*", Action = "read" }]
                }
            }
        };
    }

    private static CompiledBundle CompileTestBundle()
    {
        try
        {
            return new ErpBundleCompiler().Compile(UpdateCurrentRecordBundle());
        }
        catch (BundleCompilationException ex)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, ex.Errors), ex);
        }
    }

    private static ErpActionRequires ActionRequirements() => new()
    {
        Permissions =
        [
            new ErpGrant { Entity = "customer", Action = "update" },
            new ErpGrant { Entity = "invoice", Action = "create" }
        ]
    };

    private static ErpActionStep CreateInvoiceStep(string id, ErpActionValue amount) => new()
    {
        Id = id,
        Type = "create_record",
        Entity = "invoice",
        Values = new Dictionary<string, ErpActionValue>
        {
            ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
            ["amount"] = amount
        }
    };

    private static ErpActionStep MarkCustomerStep() => new()
    {
        Id = "mark_customer_converted",
        Type = "update_current_record",
        Set = new Dictionary<string, ErpActionValue>
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
    };

    private static ErpActionStep ReturnCreatedInvoiceStep(string id, string createStep) => new()
    {
        Id = id,
        Type = "return_result",
        Return = new ErpActionReturn
        {
            Step = createStep,
            Entity = "invoice",
            Message = "Invoice created."
        }
    };
}
