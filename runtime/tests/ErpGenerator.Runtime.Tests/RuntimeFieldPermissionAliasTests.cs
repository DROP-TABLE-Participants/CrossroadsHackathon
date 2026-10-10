using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeFieldPermissionAliasTests
{
    [Fact]
    public void Relation_field_grant_and_request_aliases_are_interchangeable()
    {
        var permissions = new RuntimePermissionService(null!);
        var relation = new RuntimeFieldDefinition(
            "quality_inspection.work_order",
            "quality_inspection",
            "work_order_id",
            "Work order",
            "relation",
            "work_order_id",
            "bigint",
            true,
            false,
            "work_order",
            [],
            null);
        var entity = new RuntimeEntityDefinition(
            "quality_inspections",
            "quality_inspection",
            "quality_inspections",
            "Quality inspections",
            "app",
            "quality_inspections",
            "inspection_number",
            null,
            [relation]);
        var aliases = new[] { "work_order", "quality_inspection.work_order", "work_order_id" };
        foreach (var grantedField in aliases)
        {
            var snapshot = new RuntimePermissionSnapshot(
                false,
                [
                    new RuntimePermissionGrant(
                        "write",
                        "quality_inspection",
                        PermissionGrantScopes.Field,
                        grantedField)
                ]);

            foreach (var requestedField in aliases)
            {
                permissions.IsFieldAllowed(snapshot, "write", entity, requestedField).Should().BeTrue(
                    "grant alias {0} and request alias {1} identify the same relation field",
                    grantedField,
                    requestedField);
            }

            permissions.IsFieldAllowed(snapshot, "write", entity, "unrelated_field").Should().BeFalse();
        }
    }
}
