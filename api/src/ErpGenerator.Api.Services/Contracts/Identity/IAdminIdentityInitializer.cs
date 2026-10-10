namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IAdminIdentityInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
