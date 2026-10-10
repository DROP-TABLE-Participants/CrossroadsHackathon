using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeOwnerActivationStatusInputModel(
    string Host,
    string Environment,
    string Email,
    string Status,
    DateTimeOffset? ActivatedAt = null);
