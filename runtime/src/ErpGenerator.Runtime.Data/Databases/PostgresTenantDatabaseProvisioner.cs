using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed class PostgresTenantDatabaseProvisioner(IOptions<RuntimeDatabaseOptions> options)
    : ITenantDatabaseProvisioner
{
    private static readonly Regex DatabaseNamePattern = new("^[a-z][a-z0-9_]{2,62}$", RegexOptions.Compiled);
    private readonly RuntimeDatabaseOptions options = options.Value;
    private readonly ITenantDatabaseCredentialStore? credentialStore;

    public PostgresTenantDatabaseProvisioner(
        IOptions<RuntimeDatabaseOptions> options,
        ITenantDatabaseCredentialStore credentialStore)
        : this(options)
    {
        this.credentialStore = credentialStore;
    }

    public async Task ProvisionAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default)
    {
        if (!options.AutoProvisionDatabases)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.AdminConnectionString))
        {
            throw new InvalidOperationException("Admin connection string is required to provision tenant databases.");
        }

        if (!DatabaseNamePattern.IsMatch(tenant.DatabaseName))
        {
            throw new InvalidOperationException($"Unsafe tenant database name '{tenant.DatabaseName}'.");
        }

        await using var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(options.AdminConnectionString));
        await connection.OpenAsync(cancellationToken);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "select 1 from pg_database where datname = @databaseName";
        existsCommand.Parameters.AddWithValue("databaseName", tenant.DatabaseName);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken) is not null;

        if (!exists)
        {
            await using var createCommand = connection.CreateCommand();
            createCommand.CommandText = $"create database {QuoteIdentifier(tenant.DatabaseName)}";
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (options.ProvisionTenantAppUsers)
        {
            await ProvisionTenantAppUserAsync(connection, tenant, cancellationToken);
        }
    }

    public async Task GrantRuntimePrivilegesAsync(
        ResolvedTenantEnvironment tenant,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (!options.ProvisionTenantAppUsers ||
            credentialStore is null ||
            !credentialStore.TryGet(tenant, out var credential))
        {
            return;
        }

        var user = QuoteIdentifier(credential.UserName);
        var statements = new[]
        {
            "revoke create on schema public from public",
            "grant usage on schema _runtime, app, audit to " + user,
            "grant select, insert, update, delete on all tables in schema _runtime, app, audit to " + user,
            "grant usage, select, update on all sequences in schema _runtime, app, audit to " + user,
            "alter default privileges in schema _runtime grant select, insert, update, delete on tables to " + user,
            "alter default privileges in schema app grant select, insert, update, delete on tables to " + user,
            "alter default privileges in schema audit grant select, insert, update, delete on tables to " + user,
            "alter default privileges in schema _runtime grant usage, select, update on sequences to " + user,
            "alter default privileges in schema app grant usage, select, update on sequences to " + user,
            "alter default privileges in schema audit grant usage, select, update on sequences to " + user
        };

        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ProvisionTenantAppUserAsync(
        NpgsqlConnection adminConnection,
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        if (credentialStore is null)
        {
            throw new InvalidOperationException("Tenant database credential store is required to provision tenant app users.");
        }

        var userName = BuildAppUserName(tenant);
        var password = credentialStore.TryGet(tenant, out var existing)
            ? ExtractPassword(existing.ConnectionString)
            : GeneratePassword();

        await using (var roleExists = adminConnection.CreateCommand())
        {
            roleExists.CommandText = "select 1 from pg_roles where rolname = @userName";
            roleExists.Parameters.AddWithValue("userName", userName);
            var exists = await roleExists.ExecuteScalarAsync(cancellationToken) is not null;

            await using var roleCommand = adminConnection.CreateCommand();
            roleCommand.CommandText = exists
                ? $"alter role {QuoteIdentifier(userName)} with login password {QuoteLiteral(password)}"
                : $"create role {QuoteIdentifier(userName)} login password {QuoteLiteral(password)}";
            await roleCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var revokePublic = adminConnection.CreateCommand())
        {
            revokePublic.CommandText = $"revoke connect on database {QuoteIdentifier(tenant.DatabaseName)} from public";
            await revokePublic.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var grantConnect = adminConnection.CreateCommand())
        {
            grantConnect.CommandText = $"grant connect on database {QuoteIdentifier(tenant.DatabaseName)} to {QuoteIdentifier(userName)}";
            await grantConnect.ExecuteNonQueryAsync(cancellationToken);
        }

        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(options.AdminConnectionString, tenant.DatabaseName))
        {
            Database = tenant.DatabaseName,
            Username = userName,
            Password = password
        };

        credentialStore.Store(new TenantDatabaseCredential(
            tenant.TenantStableId,
            tenant.Environment,
            tenant.DatabaseName,
            userName,
            builder.ConnectionString));
    }

    private string BuildAppUserName(ResolvedTenantEnvironment tenant)
    {
        var prefix = string.IsNullOrWhiteSpace(options.TenantAppUserPrefix) ? "erp_app" : options.TenantAppUserPrefix.Trim().ToLowerInvariant();
        var stableId = Regex.Replace(tenant.TenantStableId.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        if (stableId.Length == 0)
        {
            stableId = Regex.Replace(tenant.DatabaseName.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        }

        var suffix = tenant.Environment == TenantEnvironmentKind.Production ? "prod" : "staging";
        var shortId = stableId.Length > 14 ? stableId[..14] : stableId;
        var userName = $"{prefix}_{shortId}_{suffix}";
        return userName.Length <= 63 ? userName : userName[..63];
    }

    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

    private static string ExtractPassword(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).Password ?? GeneratePassword();

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
