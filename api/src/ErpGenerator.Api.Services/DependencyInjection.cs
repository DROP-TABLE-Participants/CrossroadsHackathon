using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Api.Data;
using ErpGenerator.Api.Data.Realtime;
using ErpGenerator.Api.Services.Implementations.Realtime;

namespace ErpGenerator.Api.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddErpGeneratorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddErpGeneratorData(configuration);
        services.AddDataProtection()
            .PersistKeysToDbContext<ErpGeneratorDbContext>()
            .SetApplicationName("ErpGenerator.Api");
        AddControlPlaneIdentity(services, addDefaultTokenProviders: true);
        services.AddSingleton<IControlPlaneRealtimePublisher, PostgresControlPlaneRealtimePublisher>();
        services.AddHostedService<PostgresRealtimeListener>();
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<PortalOptions>(configuration.GetSection(PortalOptions.SectionName));
        services.Configure<ClaudeGenerationOptions>(configuration.GetSection(ClaudeGenerationOptions.SectionName));
        services.Configure<GenerationOrchestrationOptions>(
            configuration.GetSection(GenerationOrchestrationOptions.SectionName));
        services.Configure<RuntimeAddressOptions>(configuration.GetSection(RuntimeAddressOptions.SectionName));
        services.Configure<InternalApiOptions>(configuration.GetSection(InternalApiOptions.SectionName));
        services.Configure<RuntimeBrokerOptions>(configuration.GetSection(RuntimeBrokerOptions.SectionName));
        services.Configure<InterviewVoiceInputOptions>(configuration.GetSection(InterviewVoiceInputOptions.SectionName));
        services.Configure<InterviewEmailOptions>(configuration.GetSection(InterviewEmailOptions.SectionName));
        services.Configure<NangoOptions>(configuration.GetSection(NangoOptions.SectionName));
        services.Configure<CustomerSessionOptions>(configuration.GetSection(CustomerSessionOptions.SectionName));
        services.AddScoped<ICustomerSessionService, CustomerSessionService>();
        services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IIdentityAccountValidator, IdentityAccountValidator>();
        services.AddScoped<ICustomerPasswordResetService, CustomerPasswordResetService>();
        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromMinutes(30));
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<IRuntimeControlPlaneService, RuntimeControlPlaneService>();
        services.AddScoped<IRuntimePromotionReceiptVerifier, RuntimePromotionReceiptVerifier>();
        services.AddScoped<IGeneratedStagingDeploymentQueue, GeneratedStagingDeploymentQueue>();
        services.AddScoped<IRuntimeDeploymentProcessor, RuntimeDeploymentProcessor>();
        services.AddScoped<IRuntimeLifecycleService, RuntimeLifecycleService>();
        services.AddScoped<ICustomerFeedbackService, CustomerFeedbackService>();
        services.AddHttpClient<IRuntimeDeploymentClient, RuntimeDeploymentClient>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<IGenerationProjectService, GenerationProjectService>();
        services.AddScoped<IGenerationWorkflowService, GenerationWorkflowService>();
        services.AddScoped<IInterviewInviteDeliveryService, InterviewInviteDeliveryService>();
        services.AddHttpClient<INangoInputSourceService, NangoInputSourceService>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IGenerationEvidenceService, GenerationEvidenceService>();
        services.AddScoped<IGenerationResumeService, GenerationResumeService>();
        services.AddScoped<IQueuedGenerationService, QueuedGenerationService>();
        services.AddScoped<IGenerationLeaseService, GenerationLeaseService>();
        services.AddSingleton<IAgentDefinitionRegistry, AgentDefinitionRegistry>();
        services.AddHostedService<AgentRegistryStartupValidator>();
        services.AddScoped<IAgentExecutionContextAccessor, AgentExecutionContextAccessor>();
        services.AddScoped<IAgentWorkItemService, AgentWorkItemService>();
        services.AddScoped<IGenerationStageHandler, CompanyUnderstandingStageHandler>();
        services.AddScoped<IGenerationStageHandler, ErpDesignStageHandler>();
        services.AddScoped<IGenerationRunOrchestrator, GenerationRunOrchestrator>();
        services.AddHttpClient<IStructuredGenerationClient, ClaudeGenerationClient>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IInterviewVoiceInputService, ElevenLabsInterviewVoiceInputService>(client =>
            client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IDemoBookingService, DemoBookingService>();
        services.AddScoped<IDemoWorkspaceService, DemoWorkspaceService>();
        services.AddScoped<IEmailDeliveryService, SmtpEmailDeliveryService>();
        services.AddScoped<INotificationDeliveryProcessor, NotificationDeliveryProcessor>();
        return services;
    }

    public static IServiceCollection AddErpGeneratorMigrationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddErpGeneratorData(configuration);
        AddControlPlaneIdentity(services, addDefaultTokenProviders: false);
        services.AddScoped<IControlPlaneInitializer, ControlPlaneInitializer>();
        return services;
    }

    private static void AddControlPlaneIdentity(
        IServiceCollection services,
        bool addDefaultTokenProviders)
    {
        var identity = services.AddIdentityCore<CustomerAccount>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ErpGeneratorDbContext>();

        if (!addDefaultTokenProviders)
        {
            return;
        }

        identity.AddDefaultTokenProviders();
    }
}
