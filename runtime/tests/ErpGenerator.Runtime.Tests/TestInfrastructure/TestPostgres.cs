using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

internal static class TestPostgres
{
    public const string EnvironmentVariableName = "ERP_GENERATOR_TEST_POSTGRES";

    public static bool TryGetAdminConnectionString(out string adminConnectionString)
    {
        adminConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariableName) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(adminConnectionString);
    }

    public static string GetRequiredAdminConnectionString()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableName} is required. Mark PostgreSQL-dependent tests with " +
                "[PostgresFact] or [PostgresTheory].");
        }

        return adminConnectionString;
    }

    public static TenantDatabaseConnectionFactory CreateConnectionFactory(string adminConnectionString) => new(
        Options.Create(new RuntimeDatabaseOptions
        {
            TenantConnectionStringTemplate = adminConnectionString
        }));

    public static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"create database {QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString));
        await connection.OpenAsync();

        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText = """
                select pg_terminate_backend(pid)
                from pg_stat_activity
                where datname = @databaseName and pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("databaseName", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = connection.CreateCommand();
        drop.CommandText = $"drop database if exists {QuoteIdentifier(databaseName)}";
        await drop.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
