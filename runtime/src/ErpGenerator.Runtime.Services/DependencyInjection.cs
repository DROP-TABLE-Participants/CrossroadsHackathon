using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Runtime.Data;
using ErpGenerator.Runtime.Services.Assistant;
using ErpGenerator.Runtime.Services.ArtificialIntelligence;
using ErpGenerator.Runtime.Services.Auth;
using ErpGenerator.Runtime.Services.DeploymentBroker;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddErpGeneratorRuntimeServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddErpGeneratorRuntimeData(configuration);
        services.Configure<ControlPlaneOptions>(configuration.GetSection("ControlPlane"));
        services.AddHttpClient<IControlPlaneClient, HttpControlPlaneClient>();
        services.AddSingleton<ErpBundleYamlLoader>();
        services.AddSingleton<ErpBundleCompiler>();
        services.AddSingleton<ErpBundleMigrationPlanner>();
        services.AddSingleton<RuntimeActiveBundleReader>();
        services.AddSingleton<IRuntimeToolCatalogService, RuntimeToolCatalogService>();
        services.AddSingleton<RuntimeSessionService>();
        services.AddSingleton<IRuntimeSessionService>(provider => provider.GetRequiredService<RuntimeSessionService>());
        services.AddSingleton<RuntimeRecordStore>();
        services.AddSingleton<IRuntimeRecordService>(provider => provider.GetRequiredService<RuntimeRecordStore>());
        services.AddSingleton<RuntimeReportStore>();
        services.AddSingleton<IRuntimeReportService>(provider => provider.GetRequiredService<RuntimeReportStore>());
        services.AddSingleton<RuntimeUserStore>();
        services.AddSingleton<IRuntimeUserService>(provider => provider.GetRequiredService<RuntimeUserStore>());
        services.AddSingleton<RuntimePermissionService>();
        services.AddSingleton<IRuntimePermissionService>(provider => provider.GetRequiredService<RuntimePermissionService>());
        services.AddSingleton<RuntimeWorkflowStore>();
        services.AddSingleton<IRuntimeWorkflowService>(provider => provider.GetRequiredService<RuntimeWorkflowStore>());
        services.AddSingleton<RuntimeActionStore>();
        services.AddSingleton<IRuntimeActionService>(provider => provider.GetRequiredService<RuntimeActionStore>());
        services.AddSingleton<RuntimeSeedRunner>();
        services.AddSingleton<IRuntimeSeedService>(provider => provider.GetRequiredService<RuntimeSeedRunner>());
        services.AddSingleton<RuntimeBundleTestRunner>();
        services.AddSingleton<IRuntimeBundleTestService>(provider => provider.GetRequiredService<RuntimeBundleTestRunner>());
        services.AddSingleton<IRuntimeDatabaseService, RuntimeDatabaseService>();
        services.AddSingleton<ITenantProvisioningService, TenantProvisioningService>();
        services.AddSingleton<IRuntimeScheduledWorkflowExecutor, RuntimeScheduledWorkflowExecutor>();
        services.AddSingleton<RuntimeScheduleCycle>();
        services.AddSingleton<IRuntimeScheduleCycle>(provider =>
            provider.GetRequiredService<RuntimeScheduleCycle>());
        services.Configure<RuntimeAssistantOptions>(configuration.GetSection(RuntimeAssistantOptions.SectionName));
        services.AddHttpClient<RuntimeClaudeClient>().RemoveAllLoggers();
        services.AddSingleton<IRuntimeAssistantModelClient>(provider => provider.GetRequiredService<RuntimeClaudeClient>());
        services.AddSingleton<RuntimeAssistantInteractionStore>();
        services.AddSingleton<ErpAssistantService>();
        services.AddSingleton<IErpAssistantService>(provider => provider.GetRequiredService<ErpAssistantService>());
        services.Configure<RuntimeDeploymentBrokerOptions>(configuration.GetSection("RuntimeDeploymentBroker"));
        services.AddSingleton(_ => new RuntimeLifecycleProductionActivationPolicy(
            configuration["RuntimeDeploymentBroker:LifecycleMachineAttestationSecret"],
            configuration["RuntimeDeploymentBroker:LifecycleSupervisorAttestationSecret"]));
        services.AddSingleton<RuntimeDeploymentBrokerAuth>();
        services.AddSingleton<IRuntimeDeploymentBrokerAuth>(provider => provider.GetRequiredService<RuntimeDeploymentBrokerAuth>());
        services.AddSingleton<RuntimeProductionPromotionReceiptIssuer>();
        services.AddSingleton<IRuntimeDeploymentBrokerService, RuntimeDeploymentBrokerService>();
        services.AddSingleton<CanonicalBundleDeploymentService>();
        services.AddSingleton<ICanonicalBundleDeploymentService>(provider => provider.GetRequiredService<CanonicalBundleDeploymentService>());
        return services;
    }
}
