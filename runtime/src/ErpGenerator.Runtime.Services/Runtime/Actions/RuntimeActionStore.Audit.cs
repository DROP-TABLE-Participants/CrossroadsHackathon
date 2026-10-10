using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeActionStore
{
    private static async Task WriteAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext? user,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        var envelope = new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["tenantStableId"] = tenant.TenantStableId,
            ["tenantSlug"] = tenant.TenantSlug,
            ["host"] = tenant.Host,
            ["environment"] = tenant.Environment.ToString(),
            ["runtimeUserId"] = user?.UserId,
            ["runtimeUserEmail"] = user?.Email,
            ["runtimeUserRole"] = user?.Role,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        await using var command = new NpgsqlCommand(
            """
            insert into audit.events (event_type, message, metadata)
            values (@eventType, @message, @metadata)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(envelope, JsonOptions);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
