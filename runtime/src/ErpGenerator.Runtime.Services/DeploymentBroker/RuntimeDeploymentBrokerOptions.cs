namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public sealed record RuntimeDeploymentBrokerOptions
{
    public string? Secret { get; init; }

    public string? LifecycleMachineAttestationSecret { get; init; }

    public string? LifecycleSupervisorAttestationSecret { get; init; }

}
