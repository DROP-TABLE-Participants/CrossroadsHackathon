using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Tenants;

public sealed class TenantDatabaseSecret : ControlPlaneEntity
{
    [MaxLength(200)] public string SecretReference { get; set; } = string.Empty;
    [MaxLength(64)] public string TenantStableId { get; set; } = string.Empty;
    [MaxLength(24)] public string Environment { get; set; } = "staging";
    [MaxLength(63)] public string DatabaseName { get; set; } = string.Empty;
    public string Ciphertext { get; set; } = string.Empty;
    [MaxLength(64)] public string Nonce { get; set; } = string.Empty;
    [MaxLength(64)] public string AuthenticationTag { get; set; } = string.Empty;
}
