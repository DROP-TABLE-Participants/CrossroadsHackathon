namespace ErpGenerator.Runtime.Tests;

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.EnvironmentVariableName)))
        {
            Skip = $"Set {TestPostgres.EnvironmentVariableName} to run this PostgreSQL integration gate.";
        }
    }
}

internal sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.EnvironmentVariableName)))
        {
            Skip = $"Set {TestPostgres.EnvironmentVariableName} to run this PostgreSQL integration gate.";
        }
    }
}
