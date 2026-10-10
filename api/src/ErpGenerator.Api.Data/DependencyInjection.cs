using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Api.Data.Context;

namespace ErpGenerator.Api.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddErpGeneratorData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ControlPlane");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings__ControlPlane is required.");
        }

        services.AddDbContext<ErpGeneratorDbContext>(options => options.UseNpgsql(connectionString));
        services.AddDbContext<AdminIdentityDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "admin_identity")));
        return services;
    }
}
