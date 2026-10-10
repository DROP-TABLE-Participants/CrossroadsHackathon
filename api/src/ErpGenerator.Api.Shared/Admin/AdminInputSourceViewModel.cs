namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminInputSourceViewModel(
    Guid Id,
    string ProviderConfigKey,
    string SourceKind,
    string Status,
    string ScopeMode,
    int RecordCount,
    int TextRecordCount,
    string? LastError,
    string? LastWarning,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSyncedAt);
