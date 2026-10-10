namespace ErpGenerator.Api.Shared.Generation;

public sealed record CreateNangoConnectSessionInputModel(
    string? ConnectorKey,
    string? DisplayName = null,
    bool AuthorizationAccepted = false);
