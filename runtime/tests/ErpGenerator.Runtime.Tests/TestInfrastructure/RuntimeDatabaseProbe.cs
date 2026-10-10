using System.Security.Cryptography;
using System.Text;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeDatabaseProbe
{
    public static async Task<bool> ColumnExistsAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string tableName,
        string columnName)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists (
              select 1
              from information_schema.columns
              where table_schema = 'app'
                and table_name = @tableName
                and column_name = @columnName
            )
            """;
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("columnName", columnName);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    public static async Task<bool> TableExistsAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string tableName)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists (
              select 1
              from information_schema.tables
              where table_schema = 'app'
                and table_name = @tableName
            )
            """;
        command.Parameters.AddWithValue("tableName", tableName);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    public static async Task<bool> ActivationTokenHashExistsAsync(string adminConnectionString, string databaseName, string token)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select token_hash from _runtime.activation_tokens where token_hash = @hash limit 1";
        command.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant());
        return await command.ExecuteScalarAsync() is not null;
    }
}
