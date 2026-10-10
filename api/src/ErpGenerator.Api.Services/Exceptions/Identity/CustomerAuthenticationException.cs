namespace ErpGenerator.Api.Services.Exceptions.Identity;

public sealed class CustomerAuthenticationException(string message) : Exception(message);
