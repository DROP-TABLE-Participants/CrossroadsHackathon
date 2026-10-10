using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Services;

namespace ErpGenerator.Api.Tests;

public sealed class AdministratorIdentityTests
{
    [Fact]
    public async Task Customer_and_administrator_with_same_email_remain_independent()
    {
        await using var fixture = await CreateFixtureAsync();

        var customer = new CustomerAccount
        {
            Id = Guid.NewGuid(),
            UserName = "owner@example.test",
            Email = "owner@example.test",
            Name = "Customer owner",
            IsActive = true
        };
        var admin = new AdminAccount
        {
            Id = Guid.NewGuid(),
            UserName = "owner@example.test",
            Email = "owner@example.test",
            Name = "Platform operator",
            IsActive = true
        };

        Assert.True((await fixture.CustomerUsers.CreateAsync(customer, "Customer!123")).Succeeded);
        Assert.True((await fixture.CustomerUsers.AddToRoleAsync(customer, "Customer")).Succeeded);
        Assert.True((await fixture.AdminUsers.CreateAsync(admin, "Administrator!123")).Succeeded);
        Assert.True((await fixture.AdminUsers.AddToRoleAsync(admin, IdentityRoleNames.PlatformAdministrator)).Succeeded);

        Assert.Equal(customer.Id, (await fixture.CustomerUsers.FindByEmailAsync("owner@example.test"))!.Id);
        Assert.Equal(admin.Id, (await fixture.AdminUsers.FindByEmailAsync("owner@example.test"))!.Id);
        Assert.NotEqual(customer.Id, admin.Id);
        Assert.False(await fixture.CustomerUsers.CheckPasswordAsync(customer, "Administrator!123"));
        Assert.False(await fixture.AdminUsers.CheckPasswordAsync(admin, "Customer!123"));
    }

    [Fact]
    public async Task Refresh_tokens_cannot_cross_identity_realms()
    {
        await using var fixture = await CreateFixtureAsync();
        var customer = await fixture.CreateCustomerAsync();
        var admin = await fixture.CreateAdministratorAsync();

        var customerSession = await fixture.CustomerSessions.CreateAsync(customer.Id, CancellationToken.None);
        var adminSession = await fixture.AdminSessions.CreateAsync(admin.Id, CancellationToken.None);

        await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
            fixture.AdminSessions.RefreshAsync(customerSession.RefreshToken, CancellationToken.None));
        await Assert.ThrowsAsync<CustomerAuthenticationException>(() =>
            fixture.CustomerSessions.RefreshAsync(adminSession.RefreshToken, CancellationToken.None));
        Assert.Single(await fixture.ControlPlane.RefreshSessions.ToListAsync());
        Assert.Single(await fixture.AdminIdentity.AdminRefreshSessions.ToListAsync());
    }

    [Fact]
    public async Task Administrator_login_never_searches_the_customer_store()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.CreateCustomerAsync("shared@example.test", "Customer!123");

        await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
            fixture.AdminAuthentication.LoginAsync(
                new LoginAdminInputModel("shared@example.test", "Customer!123"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Active_administrator_without_platform_role_cannot_login_or_create_session()
    {
        await using var fixture = await CreateFixtureAsync();
        var admin = new AdminAccount
        {
            Id = Guid.NewGuid(),
            UserName = "unprivileged@example.test",
            Email = "unprivileged@example.test",
            Name = "Unprivileged administrator identity",
            IsActive = true
        };
        Assert.True((await fixture.AdminUsers.CreateAsync(admin, "Administrator!123")).Succeeded);

        await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
            fixture.AdminAuthentication.LoginAsync(
                new LoginAdminInputModel(admin.Email, "Administrator!123"),
                CancellationToken.None));
        await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
            fixture.AdminSessions.CreateAsync(admin.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Removing_platform_role_invalidates_the_administrator_refresh_session()
    {
        await using var fixture = await CreateFixtureAsync();
        var admin = await fixture.CreateAdministratorAsync();
        var session = await fixture.AdminSessions.CreateAsync(admin.Id, CancellationToken.None);
        Assert.True((await fixture.AdminUsers.RemoveFromRoleAsync(
            admin,
            IdentityRoleNames.PlatformAdministrator)).Succeeded);

        await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
            fixture.AdminSessions.RefreshAsync(session.RefreshToken, CancellationToken.None));
        Assert.NotNull((await fixture.AdminIdentity.AdminRefreshSessions.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task Removing_platform_role_invalidates_an_existing_administrator_access_token()
    {
        await using var fixture = await CreateFixtureAsync();
        var admin = await fixture.CreateAdministratorAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim(IdentityClaimNames.AccountRealm, IdentityClaimNames.AdministratorRealm),
            new Claim(IdentityClaimNames.SecurityStamp, admin.SecurityStamp!)
        ], "test"));
        var validator = new IdentityAccountValidator(fixture.ControlPlane, fixture.AdminIdentity);

        Assert.True(await validator.IsActiveAdministratorAsync(principal, CancellationToken.None));
        Assert.True((await fixture.AdminUsers.RemoveFromRoleAsync(
            admin,
            IdentityRoleNames.PlatformAdministrator)).Succeeded);
        Assert.False(await validator.IsActiveAdministratorAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task Administrator_password_policy_does_not_overwrite_customer_identity_options()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] =
                    "Host=localhost;Database=identity-options-test;Username=test;Password=test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErpGeneratorMigrationServices(configuration);
        await using var provider = services.BuildServiceProvider();
        var identityOptions = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var adminUsers = provider.GetRequiredService<UserManager<AdminAccount>>();
        var admin = new AdminAccount();

        Assert.Equal(10, identityOptions.Password.RequiredLength);
        Assert.Equal(TimeSpan.FromMinutes(15), identityOptions.Lockout.DefaultLockoutTimeSpan);
        var elevenCharacterResults = await Task.WhenAll(adminUsers.PasswordValidators.Select(
            validator => validator.ValidateAsync(adminUsers, admin, "Strong!1234")));
        Assert.Contains(
            elevenCharacterResults.SelectMany(value => value.Errors),
            value => value.Code == "AdminPasswordTooShort");
        var twelveCharacterResults = await Task.WhenAll(adminUsers.PasswordValidators.Select(
            validator => validator.ValidateAsync(adminUsers, admin, "Strong!12345")));
        Assert.All(twelveCharacterResults, value => Assert.True(value.Succeeded));
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ErpGeneratorDbContext>(options =>
            options.UseInMemoryDatabase($"customer-{Guid.NewGuid():N}"));
        services.AddDbContext<AdminIdentityDbContext>(options =>
            options.UseInMemoryDatabase($"admin-{Guid.NewGuid():N}"));
        services.AddIdentityCore<CustomerAccount>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ErpGeneratorDbContext>();
        services.AddIdentityCore<AdminAccount>()
            .AddRoles<AdminRole>()
            .AddEntityFrameworkStores<AdminIdentityDbContext>();
        services.Configure<CustomerSessionOptions>(options => options.RefreshLifetimeDays = 7);
        services.Configure<AdminSessionOptions>(options => options.RefreshLifetimeDays = 1);
        services.AddSingleton<IAccessTokenIssuer, FakeCustomerTokenIssuer>();
        services.AddSingleton<IAdminAccessTokenIssuer, FakeAdminTokenIssuer>();
        services.AddScoped<ICustomerSessionService, CustomerSessionService>();
        services.AddScoped<IAdminSessionService, AdminSessionService>();
        services.AddScoped<IAdminAuthenticationService, AdminAuthenticationService>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var customerRoles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var adminRoles = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
        Assert.True((await customerRoles.CreateAsync(new IdentityRole<Guid>("Customer"))).Succeeded);
        Assert.True((await adminRoles.CreateAsync(
            new AdminRole { Name = IdentityRoleNames.PlatformAdministrator })).Succeeded);
        return new TestFixture(provider, scope);
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
            AdminAuthentication = scope.ServiceProvider.GetRequiredService<IAdminAuthenticationService>();
        }

        public ErpGeneratorDbContext ControlPlane { get; }
        public AdminIdentityDbContext AdminIdentity { get; }
        public UserManager<CustomerAccount> CustomerUsers { get; }
        public UserManager<AdminAccount> AdminUsers { get; }
        public ICustomerSessionService CustomerSessions { get; }
        public IAdminSessionService AdminSessions { get; }
        public IAdminAuthenticationService AdminAuthentication { get; }

        public async Task<CustomerAccount> CreateCustomerAsync(
            string email = "customer@example.test",
            string password = "Customer!123")
        {
            var customer = new CustomerAccount
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                Name = "Customer",
                IsActive = true
            };
            Assert.True((await CustomerUsers.CreateAsync(customer, password)).Succeeded);
            Assert.True((await CustomerUsers.AddToRoleAsync(customer, "Customer")).Succeeded);
            return customer;
        }

        public async Task<AdminAccount> CreateAdministratorAsync()
        {
            var admin = new AdminAccount
            {
                Id = Guid.NewGuid(),
                UserName = "admin@example.test",
                Email = "admin@example.test",
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
