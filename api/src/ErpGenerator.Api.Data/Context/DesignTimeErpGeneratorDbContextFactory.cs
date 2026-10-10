using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ErpGenerator.Api.Data.Context;

public sealed class DesignTimeErpGeneratorDbContextFactory : IDesignTimeDbContextFactory<ErpGeneratorDbContext>
{
    public ErpGeneratorDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ControlPlane")
            ?? "Host=127.0.0.1;Port=5432;Database=erpgenerator_api;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<ErpGeneratorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ErpGeneratorDbContext(options);
    }
}
