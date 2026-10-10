using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ErpGenerator.Api.Tests;

public sealed class PasswordResetSeparationTests
{
    [Fact]
    public async Task Administrator_reset_changes_only_administrator_credentials_and_sessions()
    {
        await using var fixture = await CreateFixtureAsync();
        const string email = "shared@example.test";
        var customer = await fixture.CreateCustomerAsync(email);
        var admin = await fixture.CreateAdministratorAsync(email);
        await fixture.CustomerSessions.CreateAsync(customer.Id, CancellationToken.None);
        await fixture.AdminSessions.CreateAsync(admin.Id, CancellationToken.None);

        await fixture.AdminPasswordReset.RequestAsync(
            new RequestAdminPasswordResetInputModel(email, "en"),
            CancellationToken.None);
        var resetUri = new Uri(fixture.Email.LastBody!.Split('\n').Single(line => line.StartsWith("http", StringComparison.Ordinal)));
        var query = ParseQuery(resetUri.Query);
        await fixture.AdminPasswordReset.ResetAsync(
            new ResetAdminPasswordInputModel(
                query["email"],
                query["token"],
                "ChangedAdministrator!123"),
            CancellationToken.None);

        Assert.True(await fixture.CustomerUsers.CheckPasswordAsync(customer, "Customer!123"));
        Assert.False(await fixture.AdminUsers.CheckPasswordAsync(admin, "Administrator!123"));
        Assert.True(await fixture.AdminUsers.CheckPasswordAsync(admin, "ChangedAdministrator!123"));
        Assert.All(
            await fixture.AdminIdentity.AdminRefreshSessions.ToListAsync(),
            value => Assert.NotNull(value.RevokedAt));
        Assert.All(
            await fixture.ControlPlane.RefreshSessions.ToListAsync(),
            value => Assert.Null(value.RevokedAt));
        var audits = await fixture.ControlPlane.AdminAuditRecords.OrderBy(value => value.CreatedAt).ToListAsync();
        Assert.Equal(
            new[]
            {
                "administrator_password_reset_requested",
                "administrator_password_reset_completed"
            },
            audits.Select(value => value.Action));
        Assert.All(audits, value => Assert.Equal(admin.Id, value.ActorAdminAccountId));
    }

    [Fact]
    public async Task Customer_and_administrator_reset_tokens_cannot_cross_realms()
    {
        await using var fixture = await CreateFixtureAsync();
        const string email = "shared@example.test";
        var customer = await fixture.CreateCustomerAsync(email);
        var admin = await fixture.CreateAdministratorAsync(email);

        await fixture.CustomerPasswordReset.RequestAsync(
            new RequestCustomerPasswordResetInputModel(email, "en"),
            CancellationToken.None);
        var customerToken = ResetToken(fixture.Email.LastBody!);
        await Assert.ThrowsAsync<CustomerValidationException>(() =>
            fixture.AdminPasswordReset.ResetAsync(
                new ResetAdminPasswordInputModel(email, customerToken, "ChangedAdministrator!123"),
                CancellationToken.None));

        await fixture.AdminPasswordReset.RequestAsync(
            new RequestAdminPasswordResetInputModel(email, "en"),
            CancellationToken.None);
        var adminToken = ResetToken(fixture.Email.LastBody!);
        await Assert.ThrowsAsync<CustomerValidationException>(() =>
            fixture.CustomerPasswordReset.ResetAsync(
                new ResetCustomerPasswordInputModel(email, adminToken, "ChangedCustomer!123"),
                CancellationToken.None));

        Assert.True(await fixture.CustomerUsers.CheckPasswordAsync(customer, "Customer!123"));
        Assert.True(await fixture.AdminUsers.CheckPasswordAsync(admin, "Administrator!123"));
    }

    private static string ResetToken(string emailBody)
    {
        var resetUri = new Uri(emailBody.Split('\n').Single(
            line => line.StartsWith("http", StringComparison.Ordinal)));
        return ParseQuery(resetUri.Query)["token"];
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .ToDictionary(
                value => Uri.UnescapeDataString(value[0]),
                value => Uri.UnescapeDataString(value.Length > 1 ? value[1] : string.Empty));

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName($"password-reset-test-{Guid.NewGuid():N}");
        services.AddDbContext<ErpGeneratorDbContext>(options =>
            options.UseInMemoryDatabase($"customer-reset-{Guid.NewGuid():N}"));
        services.AddDbContext<AdminIdentityDbContext>(options =>
            options.UseInMemoryDatabase($"admin-reset-{Guid.NewGuid():N}"));
        ConfigureIdentity(services.AddIdentityCore<CustomerAccount>())
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ErpGeneratorDbContext>()
            .AddDefaultTokenProviders();
        ConfigureIdentity(services.AddIdentityCore<AdminAccount>())
            .AddRoles<AdminRole>()
            .AddEntityFrameworkStores<AdminIdentityDbContext>()
            .AddDefaultTokenProviders();
        services.Configure<CustomerSessionOptions>(options => options.RefreshLifetimeDays = 7);
        services.Configure<AdminSessionOptions>(options => options.RefreshLifetimeDays = 1);
        services.Configure<PortalOptions>(options => options.PublicBaseUrl = "https://portal.example.test");
        services.AddSingleton<CapturingEmailDelivery>();
        services.AddSingleton<IEmailDeliveryService>(provider =>
            provider.GetRequiredService<CapturingEmailDelivery>());
        services.AddSingleton<IAccessTokenIssuer, FakeCustomerTokenIssuer>();
        services.AddSingleton<IAdminAccessTokenIssuer, FakeAdminTokenIssuer>();
        services.AddScoped<ICustomerSessionService, CustomerSessionService>();
        services.AddScoped<IAdminSessionService, AdminSessionService>();
        services.AddScoped<ICustomerPasswordResetService, CustomerPasswordResetService>();
        services.AddScoped<IAdminPasswordResetService, AdminPasswordResetService>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var customerRoles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var adminRoles = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
        Assert.True((await customerRoles.CreateAsync(new IdentityRole<Guid>("Customer"))).Succeeded);
        Assert.True((await adminRoles.CreateAsync(
            new AdminRole { Name = IdentityRoleNames.PlatformAdministrator })).Succeeded);
        return new TestFixture(provider, scope);
    }

    private static IdentityBuilder ConfigureIdentity(IdentityBuilder builder)
    {
        builder.Services.Configure<IdentityOptions>(settings =>
        {
            settings.Password.RequiredLength = 10;
            settings.Password.RequireLowercase = true;
            settings.Password.RequireUppercase = true;
            settings.Password.RequireDigit = true;
            settings.Password.RequireNonAlphanumeric = true;
        });
        return builder;
    }

    private sealed class CapturingEmailDelivery : IEmailDeliveryService
    {
        public bool IsEnabled => true;
        public string? LastBody { get; private set; }

        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken)
        {
            LastBody = body;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCustomerTokenIssuer : IAccessTokenIssuer
    {
        public AccessTokenViewModel Issue(AccessTokenSubject subject, IReadOnlyCollection<string> roles) =>
            new("customer-access", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
    }

    private sealed class FakeAdminTokenIssuer : IAdminAccessTokenIssuer
    {
        public AccessTokenViewModel Issue(AccessTokenSubject subject, IReadOnlyCollection<string> roles) =>
            new("admin-access", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        public TestFixture(ServiceProvider provider, AsyncServiceScope scope)
        {
            this.provider = provider;
            this.scope = scope;
            ControlPlane = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
            AdminIdentity = scope.ServiceProvider.GetRequiredService<AdminIdentityDbContext>();
            CustomerUsers = scope.ServiceProvider.GetRequiredService<UserManager<CustomerAccount>>();
            AdminUsers = scope.ServiceProvider.GetRequiredService<UserManager<AdminAccount>>();
            CustomerSessions = scope.ServiceProvider.GetRequiredService<ICustomerSessionService>();
            AdminSessions = scope.ServiceProvider.GetRequiredService<IAdminSessionService>();
            CustomerPasswordReset = scope.ServiceProvider.GetRequiredService<ICustomerPasswordResetService>();
            AdminPasswordReset = scope.ServiceProvider.GetRequiredService<IAdminPasswordResetService>();
            Email = scope.ServiceProvider.GetRequiredService<CapturingEmailDelivery>();
        }

        public ErpGeneratorDbContext ControlPlane { get; }
        public AdminIdentityDbContext AdminIdentity { get; }
        public UserManager<CustomerAccount> CustomerUsers { get; }
        public UserManager<AdminAccount> AdminUsers { get; }
        public ICustomerSessionService CustomerSessions { get; }
        public IAdminSessionService AdminSessions { get; }
        public ICustomerPasswordResetService CustomerPasswordReset { get; }
        public IAdminPasswordResetService AdminPasswordReset { get; }
        public CapturingEmailDelivery Email { get; }

        public async Task<CustomerAccount> CreateCustomerAsync(string email)
        {
            var customer = new CustomerAccount
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                Name = "Customer",
                IsActive = true
            };
            Assert.True((await CustomerUsers.CreateAsync(customer, "Customer!123")).Succeeded);
            Assert.True((await CustomerUsers.AddToRoleAsync(customer, "Customer")).Succeeded);
            return customer;
        }

        public async Task<AdminAccount> CreateAdministratorAsync(string email)
        {
            var admin = new AdminAccount
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                Name = "Administrator",
                IsActive = true
            };
            Assert.True((await AdminUsers.CreateAsync(admin, "Administrator!123")).Succeeded);
            Assert.True((await AdminUsers.AddToRoleAsync(
                admin,
                IdentityRoleNames.PlatformAdministrator)).Succeeded);
            return admin;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}
