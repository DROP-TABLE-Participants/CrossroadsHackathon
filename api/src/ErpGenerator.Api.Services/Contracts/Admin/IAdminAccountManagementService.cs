namespace ErpGenerator.Api.Services.Contracts.Admin;

public interface IAdminAccountManagementService
{
    Task<AdminAdministratorListViewModel> ListAsync(
        string? search,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
    Task<AdminAdministratorViewModel> CreateAsync(
        Guid actorId,
        string actorEmail,
        CreateAdminAdministratorInputModel input,
        CancellationToken cancellationToken);
    Task<AdminAdministratorViewModel> SetStatusAsync(
        Guid actorId,
        string actorEmail,
        Guid administratorId,
        UpdateAdminAdministratorStatusInputModel input,
        CancellationToken cancellationToken);
    Task RequestPasswordResetAsync(
        Guid actorId,
        string actorEmail,
        Guid administratorId,
        string? locale,
        CancellationToken cancellationToken);
}
