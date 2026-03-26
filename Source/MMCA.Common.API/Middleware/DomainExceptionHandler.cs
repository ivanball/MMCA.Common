using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MMCA.Common.Shared.Exceptions;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Translates <see cref="DomainException"/> into an HTTP 400 Bad Request ProblemDetails
/// response. Domain exceptions represent business rule violations that the client can
/// potentially correct and retry.
/// </summary>
/// <param name="problemDetailsService">The service used to write RFC 9457 problem details.</param>
/// <param name="logger">Logger for recording domain exceptions.</param>
public sealed class DomainExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DomainExceptionHandler> logger)
    : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
            return false;

        logger.LogWarning(domainException, "Domain exception occurred");

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = httpContext.Response.StatusCode,
                Title = "Domain Exception",
                Detail = domainException.Message
            }
        };

        return await problemDetailsService.TryWriteAsync(context);
    }
}
