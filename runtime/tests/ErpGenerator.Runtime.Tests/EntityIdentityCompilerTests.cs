using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class EntityIdentityCompilerTests
{
    [Theory]
    [InlineData("customer", "customers", "customers", "customer_archive", "entity id versus generated table name")]
    [InlineData("customer", "customers", "customer_archive", "customer", "entity name versus entity id")]
    [InlineData("customer", "customers", "customer_archive", "customers", "duplicate generated table name")]
    [InlineData("policy", "policy_records", "policys", "policy_archives", "entity id versus implicit plural alias")]
    public void Compiler_rejects_cross_entity_identity_namespace_collisions(
        string firstId,
        string firstName,
        string secondId,
        string secondName,
        string reason)
    {
        var bundle = Bundle(
            Entity(firstId, firstName),
            Entity(secondId, secondName));

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>(reason)
            .Which.Errors.Should().Contain(error =>
                error.Contains("Ambiguous entity identifier", StringComparison.Ordinal) &&
                error.Contains("must be unique across the bundle", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_allows_an_entity_to_share_its_own_id_name_and_alias_surfaces()
    {
        var bundle = Bundle(
            Entity("customer", "customers"),
            Entity("invoice", "invoices"));

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Entities.Select(entity => entity.Id).Should().BeEquivalentTo("customer", "invoice");
    }

    [Theory]
    [InlineData("billing_owner_id", "alternate_owner", "billing_owner_id")]
    [InlineData("alternate_owner", "billing_owner", "billing_owner")]
    public void Compiler_reports_cross_field_identity_collisions_before_report_metadata_compilation(
        string secondId,
        string secondName,
        string ambiguousReference)
    {
        var bundle = Bundle(Entity(
            "work_item",
            "work_items",
            Field("billing_owner", "billing_owner_id", "integer"),
            Field(secondId, secondName, "integer")));
        bundle.Reports.Add(new ErpReport
        {
            Key = "items_by_owner",
            Label = "Items by owner",
            Source = "work_item",
            GroupBy = [new ErpReportGroupBy { Field = ambiguousReference, Label = "Owner" }],
            Metrics = [new ErpReportMetric { Key = "item_count", Label = "Items", Op = "count" }]
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains($"Ambiguous field identifier '{ambiguousReference}'", StringComparison.Ordinal) &&
                error.Contains("must be unique within an entity", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("created_at")]
    [InlineData("created_by")]
    [InlineData("updated_at")]
    [InlineData("updated_by")]
    [InlineData("archived_at")]
    [InlineData("archived_by")]
    [InlineData("archive_reason")]
    public void Compiler_rejects_every_reserved_runtime_system_field_id_and_physical_name(string reserved)
    {
        var compiler = new ErpBundleCompiler();
        var idErrors = compiler.Validate(Bundle(Entity(
            "work_item",
            "work_items",
            Field(reserved, "business_value"))));
        var nameErrors = compiler.Validate(Bundle(Entity(
            "work_item",
            "work_items",
            Field("business_value", reserved))));

        idErrors.Should().Contain(error =>
            error.Contains($"'{reserved}' conflicts with a reserved runtime system field identifier", StringComparison.Ordinal));
        nameErrors.Should().Contain(error =>
            error.Contains($"'{reserved}' conflicts with a reserved runtime system field identifier", StringComparison.Ordinal));
    }

    [Fact]
    public void Declared_created_by_field_cannot_be_silently_rebound_to_the_runtime_creator_column()
    {
        var bundle = Bundle(Entity(
            "work_item",
            "work_items",
            Field("created_by", "owner_principal", "integer")));
        bundle.Roles["member"] = new ErpRole
        {
            DisplayName = "Member",
            Grants =
            [
                new ErpGrant
                {
                    Scope = PermissionGrantScopes.Record,
                    Action = "read",
                    Entity = "work_item",
                    RecordScope = new ErpRecordScope
                    {
                        Field = "created_by",
                        Source = RecordPermissionScopeSources.ActorPrincipal
                    }
                }
            ]
        };

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("created_by", StringComparison.Ordinal) &&
                error.Contains("reserved runtime system field identifier", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("customer__label")]
    [InlineData("customer__secondaryLabel")]
    public void Compiler_rejects_reserved_runtime_relation_label_field_suffixes(string reserved)
    {
        var compiler = new ErpBundleCompiler();
        var idErrors = compiler.Validate(Bundle(Entity(
            "work_item",
            "work_items",
            Field(reserved, "business_value"))));
        var nameErrors = compiler.Validate(Bundle(Entity(
            "work_item",
            "work_items",
            Field("business_value", reserved))));

        idErrors.Should().Contain(error =>
            error.Contains("reserved runtime relation-label suffix", StringComparison.Ordinal));
        nameErrors.Should().Contain(error =>
            error.Contains("reserved runtime relation-label suffix", StringComparison.Ordinal));
    }

    private static ErpEntity Entity(string id, string name, params ErpField[] fields) => new()
    {
        Id = id,
        Name = name,
        DisplayName = id.Replace('_', ' '),
        Fields = fields.Length == 0
            ? [Field("label", "label")]
            : fields.ToList()
    };

    private static ErpField Field(string id, string name, string type = "string") => new()
    {
        Id = id,
        Name = name,
        DisplayName = id.Replace('_', ' '),
        Type = type,
        Required = true
    };

    private static ErpBundle Bundle(params ErpEntity[] entities) => new()
    {
        Id = "identity_collision_bundle",
        Name = "Identity collision bundle",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "operations",
                Name = "Operations",
                Entities = entities.ToList()
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["owner"] = new()
            {
                DisplayName = "Owner",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            }
        }
    };
}
