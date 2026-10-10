using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class MultiEntityReportCompilerTests
{
    [Fact]
    public void Declared_relation_joins_compile_to_bounded_typed_metadata_and_affect_hash()
    {
        var bundle = ReportBundle();

        var compiler = new ErpBundleCompiler();
        var validation = compiler.Validate(bundle);
        validation.Should().BeEmpty(string.Join(Environment.NewLine, validation));
        var compiled = compiler.Compile(bundle);
        var report = compiled.Reports.Single();

        report.Joins.Should().BeEquivalentTo(
        [
            new ReportJoinDefinition("customer", "source", "customer_id", "customer", "inner", 1),
            new ReportJoinDefinition("region", "customer", "region_id", "region", "left", 2)
        ], options => options.WithStrictOrdering());
        report.GroupBy.Should().ContainSingle(group => group.Field == "region.name");
        report.Metrics.Should().Contain(metric => metric.Key == "total_amount" && metric.Field == "source.amount");

        var withoutJoins = ReportBundle();
        withoutJoins.Reports[0] = SingleEntityReport();
        var single = new ErpBundleCompiler().Compile(withoutJoins);
        single.Reports.Single().Joins.Should().BeEmpty();
        compiled.BundleHash.Should().NotBe(single.BundleHash);
    }

    [Theory]
    [MemberData(nameof(InvalidJoinMutations))]
    public void Invalid_or_unsafe_join_shapes_are_rejected(
        Action<ErpReport> mutate,
        string expectedError)
    {
        var bundle = ReportBundle();
        mutate(bundle.Reports[0]);

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<Action<ErpReport>, string> InvalidJoinMutations => new()
    {
        {
            report => report.Joins[0] = new ErpReportJoin
            {
                Alias = "customer; drop table app.orders",
                From = "source",
                Relation = "customer",
                Type = "inner"
            },
            "SQL-like"
        },
        {
            report => report.Joins.Add(new ErpReportJoin
            {
                Alias = "customer",
                From = "source",
                Relation = "customer",
                Type = "left"
            }),
            "duplicate join alias"
        },
        {
            report => report.Joins[0] = new ErpReportJoin
            {
                Alias = "customer",
                From = "later",
                Relation = "customer",
                Type = "inner"
            },
            "unknown or forward"
        },
        {
            report => report.Joins[0] = new ErpReportJoin
            {
                Alias = "customer",
                From = "source",
                Relation = "status",
                Type = "inner"
            },
            "not a declared relation"
        },
        {
            report => report.Joins[0] = new ErpReportJoin
            {
                Alias = "customer",
                From = "source",
                Relation = "missing_relation",
                Type = "inner"
            },
            "unknown relation field"
        },
        {
            report => report.Joins[0] = new ErpReportJoin
            {
                Alias = "customer",
                From = "source",
                Relation = "customer",
                Type = "cross"
            },
            "unsupported type"
        },
        {
            report => report.Joins.AddRange(
            [
                new ErpReportJoin { Alias = "customer_again", From = "source", Relation = "customer", Type = "left" },
                new ErpReportJoin { Alias = "customer_third", From = "source", Relation = "customer", Type = "left" }
            ]),
            "at most 3"
        },
        {
            report => report.Joins.Add(new ErpReportJoin
            {
                Alias = "region_cycle",
                From = "region",
                Relation = "parent_region",
                Type = "left"
            }),
            "maximum relation join depth"
        },
        {
            report => report.Joins[1] = new ErpReportJoin
            {
                Alias = "order_cycle",
                From = "customer",
                Relation = "primary_order",
                Type = "left"
            },
            "relation cycle"
        },
        {
            report => report.GroupBy[0] = new ErpReportGroupBy { Field = "name", Label = "Name" },
            "ambiguous"
        },
        {
            report => report.Metrics[0] = new ErpReportMetric
            {
                Key = "bad_metric",
                Label = "Bad metric",
                Op = "sum",
                Field = "region.missing"
            },
            "unknown field"
        },
        {
            report => report.Filters[0] = new ErpReportFilter
            {
                Field = "region.name.extra",
                Op = "eq",
                Value = "EU"
            },
            "alias-qualified"
        }
    };

    private static ErpBundle ReportBundle() => new()
    {
        Id = "multi_entity_reports",
        Name = "Multi entity reports",
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
                        Id = "order",
                        Name = "orders",
                        DisplayName = "Orders",
                        Fields =
                        [
                            Field("order_number", "string"),
                            Field("status", "enum", options: ["open", "closed"]),
                            Field("amount", "money"),
                            Field("customer_id", "relation", references: "customer")
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "customer",
                        Name = "customers",
                        DisplayName = "Customers",
                        Fields =
                        [
                            Field("name", "string"),
                            Field("status", "enum", options: ["active", "inactive"]),
                            Field("region_id", "relation", references: "region"),
                            Field("primary_order_id", "relation", references: "order", required: false)
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "region",
                        Name = "regions",
                        DisplayName = "Regions",
                        Fields =
                        [
                            Field("name", "string"),
                            Field("code", "string"),
                            Field("parent_region_id", "relation", references: "region", required: false)
                        ]
                    }
                ]
            }
        ],
        Reports = [JoinedReport()],
        Roles = new Dictionary<string, ErpRole>
        {
            ["analyst"] = new()
            {
                DisplayName = "Analyst",
                Grants =
                [
                    new ErpGrant { Action = "read", Entity = "order" },
                    new ErpGrant { Action = "read", Entity = "customer" },
                    new ErpGrant { Action = "read", Entity = "region" }
                ]
            }
        }
    };

    private static ErpReport JoinedReport() => new()
    {
        Key = "sales_by_region",
        Label = "Sales by region",
        Source = "order",
        Joins =
        [
            new ErpReportJoin { Alias = "customer", From = "source", Relation = "customer", Type = "inner" },
            new ErpReportJoin { Alias = "region", From = "customer", Relation = "region", Type = "left" }
        ],
        GroupBy = [new ErpReportGroupBy { Field = "region.name", Label = "Region" }],
        Metrics =
        [
            new ErpReportMetric { Key = "total_amount", Label = "Total amount", Op = "sum", Field = "source.amount" },
            new ErpReportMetric
            {
                Key = "active_orders",
                Label = "Active orders",
                Op = "count",
                Filter = new ErpReportFilter { Field = "customer.status", Op = "eq", Value = "active" }
            }
        ],
        Filters = [new ErpReportFilter { Field = "source.status", Op = "eq", Value = "open" }]
    };

    private static ErpReport SingleEntityReport() => new()
    {
        Key = "order_totals",
        Label = "Order totals",
        Source = "order",
        GroupBy = [new ErpReportGroupBy { Field = "status", Label = "Status" }],
        Metrics = [new ErpReportMetric { Key = "total_amount", Label = "Total amount", Op = "sum", Field = "amount" }],
        Filters = [new ErpReportFilter { Field = "status", Op = "eq", Value = "open" }]
    };

    private static ErpField Field(
        string key,
        string type,
        string? references = null,
        IReadOnlyList<string>? options = null,
        bool required = true) =>
        new()
        {
            Id = key,
            Name = key,
            DisplayName = key.Replace('_', ' '),
            Type = type,
            Required = required,
            References = references,
            Options = options?.ToList() ?? []
        };
}
