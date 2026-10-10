using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ErpGenerator.Api.Services;
using ErpGenerator.Api.Services.Contracts.Identity;

namespace ErpGenerator.Api.Tests;

public sealed class MigrationDependencyInjectionTests
{
    [Fact]
    public void Migration_registration_contains_initializer_without_hosted_workers()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] =
                    "Host=localhost;Database=erpgenerator_test;Username=postgres;Password=postgres"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddErpGeneratorMigrationServices(configuration);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IControlPlaneInitializer));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAdminIdentityInitializer));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ErpGeneratorDbContext));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AdminIdentityDbContext));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }
}
