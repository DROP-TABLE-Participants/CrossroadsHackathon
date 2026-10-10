namespace ErpGenerator.Api.Data.Realtime;

public sealed record ControlPlaneProjectChange(
    Guid ProjectId,
    Guid? CustomerId,
    bool Removed);
