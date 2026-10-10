using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ErpGenerator.Api.Services;
using ErpGenerator.Api.Services.Contracts.Identity;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddErpGeneratorMigrationServices(builder.Configuration);

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var logger = scope.ServiceProvider
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("ErpGenerator.Api.MigrationService");

logger.LogInformation("Applying ErpGenerator control-plane migrations and initialization.");
try
{
    await scope.ServiceProvider
        .GetRequiredService<IControlPlaneInitializer>()
        .InitializeAsync();
    logger.LogInformation("ErpGenerator control-plane migrations and initialization completed.");
    return 0;
}
catch (Exception exception)
{
    logger.LogCritical(exception, "ErpGenerator control-plane migration failed.");
    return 1;
}
