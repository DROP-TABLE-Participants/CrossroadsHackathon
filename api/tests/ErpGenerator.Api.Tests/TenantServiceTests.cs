using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Tests;

public sealed class TenantServiceTests
{
    [Fact]
    public async Task List_returns_only_the_customers_tenants()
    {
        await using var dbContext = CreateContext();
        var firstCustomer = Customer("first@example.com");
        var secondCustomer = Customer("second@example.com");
        dbContext.AddRange(firstCustomer, secondCustomer);
        dbContext.Tenants.AddRange(
            Tenant(firstCustomer, "first"),
            Tenant(secondCustomer, "second"));
        await dbContext.SaveChangesAsync();

        var result = await Service(dbContext).ListAsync(firstCustomer.Id, CancellationToken.None);

        Assert.Collection(result.Tenants, tenant => Assert.Equal("first", tenant.Slug));
    }

    [Fact]
    public async Task Create_uses_an_immutable_stable_identifier_separate_from_the_slug()
    {
        await using var dbContext = CreateContext();
        var customer = Customer("owner@example.com");
        dbContext.Add(customer);
        await dbContext.SaveChangesAsync();

        var result = await Service(dbContext).CreateAsync(
            customer.Id,
            new CreateTenantInputModel("Acme Manufacturing", "acme"),
            CancellationToken.None);

        Assert.Equal("acme", result.Tenant.Slug);
        Assert.NotEqual(result.Tenant.Slug, result.Tenant.StableId);
        Assert.Matches("^[a-f0-9]{32}$", result.Tenant.StableId);
        var environment = Assert.Single(await dbContext.TenantEnvironments.ToListAsync());
        Assert.Equal(TenantDatabaseNamePolicy.BuildExpected(result.Tenant.StableId, "erp"), environment.DatabaseName);
    }

    [Theory]
    [InlineData("crossroads")]
    [InlineData("API")]
    public async Task Create_rejects_reserved_hosts_without_creating_a_tenant(string slug)
    {
        await using var dbContext = CreateContext();
        var service = new TenantService(dbContext, Options.Create(new RuntimeAddressOptions
        {
            ReservedTenantSlugs = " Crossroads, api "
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            Guid.NewGuid(), new CreateTenantInputModel("Company", slug), CancellationToken.None));

        Assert.Equal("This tenant slug is unavailable.", error.Message);
        Assert.Empty(dbContext.Tenants);
        Assert.Empty(dbContext.TenantEnvironments);
    }

    private static ErpGeneratorDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ErpGeneratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static TenantService Service(ErpGeneratorDbContext dbContext) => new(
        dbContext,
        Options.Create(new RuntimeAddressOptions()));

    private static CustomerAccount Customer(string email) => new()
    {
        Email = email,
        NormalizedEmail = email,
        Name = email,
        PasswordHash = "unused"
    };

    private static Tenant Tenant(CustomerAccount owner, string slug) => new()
    {
        StableId = Guid.NewGuid().ToString("N"),
        Slug = slug,
        CompanyName = slug,
        Owner = owner,
        OwnerId = owner.Id
    };
}
