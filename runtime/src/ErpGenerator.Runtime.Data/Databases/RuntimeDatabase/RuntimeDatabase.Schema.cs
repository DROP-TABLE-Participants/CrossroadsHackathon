using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    private async Task ExecuteBundleStatementsAsync(
        ResolvedTenantEnvironment tenant,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompiledBundle bundle,
        CancellationToken cancellationToken)
    {
        await EnsureBundleVersionIdentityCompatibleAsync(connection, transaction, bundle, cancellationToken);

        foreach (var statement in sqlBuilder.BuildDeploymentStatements(bundle))
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await provisioner.GrantRuntimePrivilegesAsync(tenant, connection, transaction, cancellationToken);
    }
}
