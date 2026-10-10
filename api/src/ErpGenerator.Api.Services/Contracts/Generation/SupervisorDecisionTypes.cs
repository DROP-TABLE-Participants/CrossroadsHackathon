namespace ErpGenerator.Api.Services.Contracts.Generation;

public static class SupervisorDecisionTypes
{
    public const string Accept = "accept";
    public const string RepairSameAgent = "repair_same_agent";
    public const string RouteToAgent = "route_to_agent";
    public const string RouteToPreviousStage = "route_to_previous_stage";
    public const string RequestEvidence = "request_evidence";
    public const string RequestInterview = "request_interview";
    public const string Fail = "fail";
}
