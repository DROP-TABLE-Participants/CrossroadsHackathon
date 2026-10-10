namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IGenerationLeaseService
{
    Task<int> RecoverExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<GenerationLeaseClaim?> ClaimNextAsync(
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<bool> RenewAsync(
        Guid runId,
        Guid leaseId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
