namespace ErpGenerator.Api.Services.Exceptions.Identity;

public sealed class CustomerConflictException(string message) : Exception(message);
