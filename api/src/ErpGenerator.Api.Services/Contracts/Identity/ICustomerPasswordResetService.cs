namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface ICustomerPasswordResetService
{
    Task RequestAsync(RequestCustomerPasswordResetInputModel input, CancellationToken cancellationToken);
    Task ResetAsync(ResetCustomerPasswordInputModel input, CancellationToken cancellationToken);
}
