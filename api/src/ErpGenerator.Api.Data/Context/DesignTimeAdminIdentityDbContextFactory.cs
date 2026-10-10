using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ErpGenerator.Api.Data.Context;

public sealed class DesignTimeAdminIdentityDbContextFactory : IDesignTimeDbContextFactory<AdminIdentityDbContext>
{
    public AdminIdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ControlPlane")
            ?? "Host=localhost;Port=5432;Database=erpgenerator;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AdminIdentityDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "admin_identity"))
            .Options;
        return new AdminIdentityDbContext(options);
    }
}
