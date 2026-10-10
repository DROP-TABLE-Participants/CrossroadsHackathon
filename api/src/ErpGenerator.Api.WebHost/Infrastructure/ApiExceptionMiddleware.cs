using ErpGenerator.Api.Shared.Common;

namespace ErpGenerator.Api.WebHost.Infrastructure;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (CustomerValidationException exception)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (ArgumentException exception)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (CustomerAuthenticationException exception)
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, exception.Message);
        }
        catch (AdminAuthenticationException exception)
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, exception.Message);
        }
        catch (CustomerConflictException exception)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            await WriteAsync(context, StatusCodes.Status404NotFound, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled API request failure for {Method} {Path}.", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "The request could not be completed.");
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ApiErrorViewModel(message));
    }
}
