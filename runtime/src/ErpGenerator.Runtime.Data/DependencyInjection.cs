using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Data.Databases.Backups;
using ErpGenerator.Runtime.Data.Databases.DeploymentLocks;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Scheduling;
using ErpGenerator.Runtime.Data.Databases.Staging;

namespace ErpGenerator.Runtime.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddErpGeneratorRuntimeData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RuntimeDatabaseOptions>(configuration.GetSection("RuntimeDatabase"));
        services.Configure<TenantDatabaseCredentialStoreOptions>(configuration.GetSection("TenantDatabaseCredentialStore"));
        services.Configure<TenantDatabaseBackupOptions>(configuration.GetSection("RuntimeBackups"));
        services.Configure<TenantDeploymentLockOptions>(configuration.GetSection("RuntimeDeployments"));
        services.Configure<RuntimeScheduleOptions>(configuration.GetSection("RuntimeScheduler"));

        services.AddHttpClient<ControlPlaneTenantDatabaseCredentialStore>();
        services.AddSingleton<ITenantDatabaseCredentialStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TenantDatabaseCredentialStoreOptions>>().Value;
            return options.Mode == TenantDatabaseCredentialStoreMode.ControlPlaneEncrypted
                ? provider.GetRequiredService<ControlPlaneTenantDatabaseCredentialStore>()
                : new InMemoryTenantDatabaseCredentialStore();
        });
        services.AddSingleton<ITenantDatabaseConnectionFactory, TenantDatabaseConnectionFactory>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<PostgresDumpTenantDatabaseBackupProvider>();
        services.AddSingleton<LocalFileTenantDatabaseBackupStorage>();
        services.AddSingleton<S3CompatibleTenantDatabaseBackupStorage>();
        services.AddSingleton<ITenantDatabaseBackupStorage>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TenantDatabaseBackupOptions>>().Value;
            return options.StorageMode == TenantDatabaseBackupStorageMode.S3Compatible
                ? provider.GetRequiredService<S3CompatibleTenantDatabaseBackupStorage>()
                : provider.GetRequiredService<LocalFileTenantDatabaseBackupStorage>();
        });
        services.AddSingleton<ITenantDatabaseBackupService, TenantDatabaseBackupService>();
        services.AddSingleton<TenantDeploymentLockService>();
        services.AddSingleton<RuntimeSqlBuilder>();
        services.AddSingleton<RuntimeDatabase>();
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<RuntimeScheduleOptions>>().Value);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<RuntimeScheduleStore>();
        services.AddSingleton<ITenantDatabaseProvisioner>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RuntimeDatabaseOptions>>().Value;
            return options.AutoProvisionDatabases
                ? new PostgresTenantDatabaseProvisioner(
                    Options.Create(options),
                    provider.GetRequiredService<ITenantDatabaseCredentialStore>())
                : new NoOpTenantDatabaseProvisioner();
        });
        services.AddSingleton<ITenantStagingOperationFence, PostgresTenantStagingOperationFence>();
        services.AddSingleton<ITenantStagingDatabaseResetter, PostgresTenantStagingDatabaseResetter>();

        return services;
    }
}
