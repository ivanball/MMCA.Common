using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Catch-all exception handler that converts any unhandled exception into an HTTP 500
/// ProblemDetails response. Must be registered last in the exception handler pipeline so
/// that more specific handlers (domain, validation, etc.) get first chance.
/// </summary>
/// <param name="problemDetailsService">The service used to write RFC 9457 problem details.</param>
/// <param name="logger">Logger for recording unhandled exceptions.</param>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception occurred");

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = httpContext.Response.StatusCode,
                Title = "Internal Server Error",
                Detail = "An error occurred while processing your request. Please try again"
            }
        });
    }
}
