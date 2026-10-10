using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Api.Shared.Identity;

namespace ErpGenerator.Api.Tests;

public sealed class CustomerServiceTests
{
    [Fact]
    public async Task Guest_is_passwordless_customer_and_resumes_with_refresh()
    {
        await using var fixture = await CreateFixtureAsync();
        var guest = await fixture.Service.CreateGuestAsync(CancellationToken.None);
        var saved = await fixture.DbContext.CustomerAccounts.SingleAsync();
        Assert.True(saved.IsGuest);
        Assert.Null(saved.PasswordHash);
        Assert.EndsWith("@demo.invalid", saved.Email);
        Assert.Equal("Customer", guest.User.Role);
        Assert.True(guest.User.IsGuest);
        var resumed = await fixture.Service.RefreshAsync(new(guest.RefreshToken), CancellationToken.None);
        Assert.Equal(guest.User.Id, resumed.User.Id);
        Assert.Single(await fixture.DbContext.CustomerAccounts.ToListAsync());
    }
    [Fact]
    public async Task Register_uses_identity_normalization_hashing_and_customer_role()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.RegisterAsync(
            new RegisterCustomerInputModel("  Ada Lovelace  ", " ADA@EXAMPLE.COM ", "Secure!123", null, "Analytical Engines", null),
            CancellationToken.None);

        var saved = await fixture.DbContext.CustomerAccounts.SingleAsync();
        Assert.Equal("ada@example.com", saved.Email);
        Assert.Equal("ADA@EXAMPLE.COM", saved.NormalizedEmail);
        Assert.Equal("Ada Lovelace", saved.Name);
        Assert.NotEqual("Secure!123", saved.PasswordHash);
        Assert.Equal(saved.Id, result.User.Id);
        Assert.Equal("Customer", result.User.Role);
        Assert.NotEmpty(result.RefreshToken);
        Assert.Single(await fixture.DbContext.RefreshSessions.ToListAsync());
    }

    [Fact]
    public async Task Register_rejects_duplicate_normalized_email()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.Service.RegisterAsync(
            new RegisterCustomerInputModel("Ada", "ada@example.com", "Secure!123", null, null, null),
            CancellationToken.None);

        await Assert.ThrowsAsync<CustomerConflictException>(() => fixture.Service.RegisterAsync(
            new RegisterCustomerInputModel("Other Ada", " ADA@example.com ", "Other!1234", null, null, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task Login_rejects_an_invalid_password_and_records_a_failed_attempt()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.Service.RegisterAsync(
            new RegisterCustomerInputModel("Ada", "ada@example.com", "Secure!123", null, null, null),
            CancellationToken.None);

        await Assert.ThrowsAsync<CustomerAuthenticationException>(() => fixture.Service.LoginAsync(
            new LoginCustomerInputModel("ada@example.com", "wrong-password"),
            CancellationToken.None));

        var saved = await fixture.DbContext.CustomerAccounts.SingleAsync();
        Assert.Equal(1, saved.AccessFailedCount);
    }

    [Fact]
    public async Task Refresh_rotates_the_stored_token_and_rejects_reuse()
    {
        await using var fixture = await CreateFixtureAsync();
        var registered = await fixture.Service.RegisterAsync(
            new RegisterCustomerInputModel("Ada", "ada@example.com", "Secure!123", null, null, null),
            CancellationToken.None);

        var refreshed = await fixture.Service.RefreshAsync(
            new RefreshCustomerSessionInputModel(registered.RefreshToken),
            CancellationToken.None);

        Assert.NotEqual(registered.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(2, await fixture.DbContext.RefreshSessions.CountAsync());
        await Assert.ThrowsAsync<CustomerAuthenticationException>(() => fixture.Service.RefreshAsync(
            new RefreshCustomerSessionInputModel(registered.RefreshToken),
            CancellationToken.None));
        Assert.All(await fixture.DbContext.RefreshSessions.ToListAsync(), value => Assert.NotNull(value.RevokedAt));
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ErpGeneratorDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddIdentityCore<CustomerAccount>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ErpGeneratorDbContext>();
        services.Configure<CustomerSessionOptions>(options => options.RefreshLifetimeDays = 7);
        services.AddSingleton<IAccessTokenIssuer, FakeTokenIssuer>();
        services.AddScoped<ICustomerSessionService, CustomerSessionService>();
        services.AddScoped<ICustomerService, CustomerService>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>("Customer"))).Succeeded);
        return new TestFixture(
            provider,
            scope,
            scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>(),
            scope.ServiceProvider.GetRequiredService<ICustomerService>());
    }

    private sealed class FakeTokenIssuer : IAccessTokenIssuer
    {
        public AccessTokenViewModel Issue(AccessTokenSubject subject, IReadOnlyCollection<string> roles) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
    }

    private sealed class TestFixture(
        ServiceProvider provider,
        AsyncServiceScope scope,
        ErpGeneratorDbContext dbContext,
        ICustomerService service) : IAsyncDisposable
    {
        public ErpGeneratorDbContext DbContext { get; } = dbContext;
        public ICustomerService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}
