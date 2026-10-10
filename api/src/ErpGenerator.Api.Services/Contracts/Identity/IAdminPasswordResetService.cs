namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IAdminPasswordResetService
{
    Task RequestAsync(RequestAdminPasswordResetInputModel input, CancellationToken cancellationToken);
    Task ResetAsync(ResetAdminPasswordInputModel input, CancellationToken cancellationToken);
}
