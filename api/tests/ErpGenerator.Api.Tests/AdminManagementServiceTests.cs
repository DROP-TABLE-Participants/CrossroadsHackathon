using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ErpGenerator.Api.Services.Contracts.Admin;
using ErpGenerator.Api.Services.Implementations.Admin;

namespace ErpGenerator.Api.Tests;

public sealed class AdminManagementServiceTests
{
    [Fact]
    public async Task Administrator_cannot_deactivate_their_own_account()
    {
        await using var fixture = await Fixture.CreateAsync();
        var administrator = await fixture.CreateAdministratorAsync("admin@example.test");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Administrators.SetStatusAsync(
                administrator.Id,
                administrator.Email!,
                administrator.Id,
                new UpdateAdminAdministratorStatusInputModel(false),
                CancellationToken.None));

        Assert.Equal("You cannot deactivate your own administrator account.", exception.Message);
        Assert.True((await fixture.AdminIdentity.AdminAccounts.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Last_active_administrator_cannot_be_deactivated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var administrator = await fixture.CreateAdministratorAsync("only@example.test");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Administrators.SetStatusAsync(
                Guid.NewGuid(),
                "actor@example.test",
                administrator.Id,
                new UpdateAdminAdministratorStatusInputModel(false),
                CancellationToken.None));

        Assert.Equal("The last active administrator cannot be deactivated.", exception.Message);
    }

    [Fact]
    public async Task Deactivating_an_administrator_revokes_sessions_and_writes_an_audit_record()
    {
        await using var fixture = await Fixture.CreateAsync();
        var actor = await fixture.CreateAdministratorAsync("actor@example.test");
        var target = await fixture.CreateAdministratorAsync("target@example.test");

        var updated = await fixture.Administrators.SetStatusAsync(
            actor.Id,
            actor.Email!,
            target.Id,
            new UpdateAdminAdministratorStatusInputModel(false),
            CancellationToken.None);

        Assert.False(updated.IsActive);
        Assert.Equal(target.Id, fixture.AdminSessions.RevokedAdministratorId);
        var audit = await fixture.ControlPlane.AdminAuditRecords.SingleAsync();
        Assert.Equal(actor.Id, audit.ActorAdminAccountId);
        Assert.Equal("administrator_status_changed", audit.Action);
        Assert.Equal(target.Id.ToString(), audit.ResourceId);
    }

    [Fact]
    public async Task Customer_deactivation_revokes_refresh_sessions_and_is_audited()
    {
        await using var fixture = await Fixture.CreateAsync();
        var customer = new CustomerAccount
        {
            Id = Guid.NewGuid(),
            Email = "customer@example.test",
            UserName = "customer@example.test",
            Name = "Customer",
            IsActive = true
        };
        customer.RefreshSessions.Add(new RefreshSession
        {
            Id = Guid.NewGuid(),
            CustomerAccountId = customer.Id,
            TokenHash = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        fixture.ControlPlane.CustomerAccounts.Add(customer);
        await fixture.ControlPlane.SaveChangesAsync();

        var service = new AdminCustomerService(fixture.ControlPlane, new FakeCustomerPasswordResetService());
        var updated = await service.SetStatusAsync(
            Guid.NewGuid(),
            "admin@example.test",
            customer.Id,
            new UpdateAdminCustomerStatusInputModel(false),
            CancellationToken.None);

        Assert.False(updated.IsActive);
        Assert.NotNull((await fixture.ControlPlane.RefreshSessions.SingleAsync()).RevokedAt);
        Assert.Equal("customer_status_changed", (await fixture.ControlPlane.AdminAuditRecords.SingleAsync()).Action);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private Fixture(ServiceProvider provider, AsyncServiceScope scope)
        {
            this.provider = provider;
            this.scope = scope;
            ControlPlane = scope.ServiceProvider.GetRequiredService<ErpGeneratorDbContext>();
            AdminIdentity = scope.ServiceProvider.GetRequiredService<AdminIdentityDbContext>();
            AdminUsers = scope.ServiceProvider.GetRequiredService<UserManager<AdminAccount>>();
            AdminSessions = new FakeAdminSessionService();
            Administrators = new AdminAccountManagementService(
                AdminIdentity,
                ControlPlane,
                AdminUsers,
                AdminSessions,
                new FakeAdminPasswordResetService());
        }

        public ErpGeneratorDbContext ControlPlane { get; }
        public AdminIdentityDbContext AdminIdentity { get; }
        public UserManager<AdminAccount> AdminUsers { get; }
        public FakeAdminSessionService AdminSessions { get; }
        public AdminAccountManagementService Administrators { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ErpGeneratorDbContext>(options =>
                options.UseInMemoryDatabase($"control-plane-{Guid.NewGuid():N}"));
            services.AddDbContext<AdminIdentityDbContext>(options =>
                options.UseInMemoryDatabase($"admin-identity-{Guid.NewGuid():N}"));
            services.AddIdentityCore<AdminAccount>()
                .AddRoles<AdminRole>()
                .AddEntityFrameworkStores<AdminIdentityDbContext>();
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
            Assert.True((await roles.CreateAsync(
                new AdminRole { Name = IdentityRoleNames.PlatformAdministrator })).Succeeded);
            return new Fixture(provider, scope);
        }

        public async Task<AdminAccount> CreateAdministratorAsync(string email)
        {
            var administrator = new AdminAccount
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserName = email,
                Name = email.Split('@')[0],
                IsActive = true
            };
            Assert.True((await AdminUsers.CreateAsync(administrator)).Succeeded);
            Assert.True((await AdminUsers.AddToRoleAsync(
                administrator,
                IdentityRoleNames.PlatformAdministrator)).Succeeded);
            return administrator;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class FakeAdminSessionService : IAdminSessionService
    {
        public Guid? RevokedAdministratorId { get; private set; }
        public Task<AdminAuthenticationViewModel> CreateAsync(Guid adminId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<AdminAuthenticationViewModel> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RevokeAllAsync(Guid adminId, CancellationToken cancellationToken)
        {
            RevokedAdministratorId = adminId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdminPasswordResetService : IAdminPasswordResetService
    {
        public Task RequestAsync(RequestAdminPasswordResetInputModel input, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task ResetAsync(ResetAdminPasswordInputModel input, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeCustomerPasswordResetService : ICustomerPasswordResetService
    {
        public Task RequestAsync(RequestCustomerPasswordResetInputModel input, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task ResetAsync(ResetCustomerPasswordInputModel input, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
