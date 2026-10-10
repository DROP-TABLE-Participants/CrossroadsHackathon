namespace ErpGenerator.Api.Services.Exceptions.Identity;

public sealed class CustomerValidationException(string message) : Exception(message);
