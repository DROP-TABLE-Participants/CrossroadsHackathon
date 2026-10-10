namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public interface IRuntimeDeploymentBrokerAuth
{
    bool IsAuthorized(string? providedSecret);
}
