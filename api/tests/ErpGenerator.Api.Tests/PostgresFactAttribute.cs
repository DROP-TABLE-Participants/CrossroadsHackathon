namespace ErpGenerator.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("ERP_GENERATOR_TEST_POSTGRES")))
        {
            Skip =
                "Set ERP_GENERATOR_TEST_POSTGRES to run the PostgreSQL orchestration suite.";
        }
    }
}
