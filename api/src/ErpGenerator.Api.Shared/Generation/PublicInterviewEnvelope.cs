namespace ErpGenerator.Api.Shared.Generation;

public sealed record PublicInterviewEnvelope(PublicInterviewViewModel Interview, Guid? SessionId = null);
