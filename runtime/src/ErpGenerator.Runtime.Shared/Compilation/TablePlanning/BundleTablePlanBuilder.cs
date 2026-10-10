using System.Security.Cryptography;
using System.Text;
using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Shared.Compilation.TablePlanning;

internal static class BundleTablePlanBuilder
{
    private const int MaxPostgresIdentifierLength = 63;
    private const int BoundedNameHashLength = 12;

    public static IReadOnlyList<TablePlan> Build(
        IReadOnlyList<EntityDefinition> entities,
        IReadOnlyList<RoleDefinition> roles) =>
        entities
            .Select(entity => ToTablePlan(entity, entities, roles))
            .OrderBy(table => table.TableName, StringComparer.Ordinal)
            .ToArray();

    static TablePlan ToTablePlan(
        EntityDefinition entity,
        IReadOnlyList<EntityDefinition> entities,
        IReadOnlyList<RoleDefinition> roles)
    {
        var columns = new List<ColumnPlan>
        {
            new("id", "bigserial", false, true),
            new("created_at", "timestamptz", false, true),
            new("created_by", "bigint", true, true),
            new("updated_at", "timestamptz", false, true),
            new("updated_by", "bigint", true, true),
            new("archived_at", "timestamptz", true, true),
            new("archived_by", "bigint", true, true),
            new("archive_reason", "text", true, true)
        };

        columns.AddRange(entity.Fields.Where(field => field.Computed is null).Select(field =>
            new ColumnPlan(field.ColumnName, field.SqlType, !field.Required)));

        var uniqueIndexes = entity.Fields
            .Where(field => field.Unique && field.Computed is null)
            .Select(field => new IndexPlan($"ux_{entity.TableName}_{field.ColumnName}", [field.ColumnName], true))
            .ToArray();
        var recordScopeIndexes = roles
            .SelectMany(role => role.Grants)
            .Where(grant =>
                grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase) &&
                grant.Entity.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) &&
                grant.RecordScope is not null)
            .Select(grant => ResolveRecordScopeColumn(entity, grant.RecordScope!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(column => new IndexPlan(
                BoundedIndexName(entity.TableName, column),
                [column],
                false))
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .ToArray();
        var relationIndexes = entity.Relationships
            .Select(relationship => entity.Fields.Single(field =>
                field.Id.Equals(relationship.Field, StringComparison.Ordinal)).ColumnName)
            .Select(column => new IndexPlan(
                BoundedName($"ix_{entity.TableName}_{column}_relation"),
                [column],
                false))
            .ToArray();
        var indexes = uniqueIndexes
            .Concat(recordScopeIndexes)
            .Concat(relationIndexes)
            .DistinctBy(index => index.Name, StringComparer.Ordinal)
            .ToArray();
        var foreignKeys = entity.Relationships.Select(relationship =>
        {
            var field = entity.Fields.Single(candidate =>
                candidate.Id.Equals(relationship.Field, StringComparison.Ordinal));
            var target = entities.Single(candidate =>
                candidate.Id.Equals(relationship.TargetEntity, StringComparison.Ordinal));
            return new ForeignKeyPlan(
                BoundedName($"fk_{entity.TableName}_{field.ColumnName}_{target.TableName}"),
                field.ColumnName,
                "app",
                target.TableName,
                "id",
                relationship.ArchiveBehavior);
        }).ToArray();

        return new TablePlan("app", entity.TableName, columns, indexes)
        {
            ForeignKeys = foreignKeys
        };
    }

    static string ResolveRecordScopeColumn(EntityDefinition entity, RecordScopeDefinition recordScope) =>
        recordScope.Field.Equals("created_by", StringComparison.OrdinalIgnoreCase)
            ? "created_by"
            : entity.Fields.Single(field =>
                field.Id.Equals(recordScope.Field, StringComparison.OrdinalIgnoreCase)).ColumnName;

    static string BoundedIndexName(string tableName, string columnName)
    {
        return BoundedName($"ix_{tableName}_{columnName}_record_scope");
    }

    static string BoundedName(string candidate)
    {
        if (candidate.Length <= MaxPostgresIdentifierLength)
        {
            return candidate;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)))
            .ToLowerInvariant()[..BoundedNameHashLength];
        var prefixLength = MaxPostgresIdentifierLength - BoundedNameHashLength - 1;
        return $"{candidate[..prefixLength]}_{hash}";
    }
}
