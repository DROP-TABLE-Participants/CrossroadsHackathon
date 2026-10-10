namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IControlPlaneInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
