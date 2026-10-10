using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public sealed class RuntimeDeploymentBrokerAuth(IOptions<RuntimeDeploymentBrokerOptions> options)
    : IRuntimeDeploymentBrokerAuth
{
    public const string HeaderName = "x-erpgenerator-deployment-broker-secret";

    private readonly string? configuredSecret = options.Value.Secret;

    public bool IsAuthorized(string? providedSecret) =>
        IsAuthorized(providedSecret, configuredSecret);

    public static bool IsAuthorized(string? providedSecret, string? configuredSecret)
    {
        if (string.IsNullOrWhiteSpace(providedSecret) || string.IsNullOrWhiteSpace(configuredSecret))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedSecret);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredSecret);
        return providedBytes.Length == configuredBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }
}
