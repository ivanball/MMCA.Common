using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Persistence.Interceptors;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Catch-all exception handler that converts any unhandled exception into an HTTP 500
/// ProblemDetails response. Must be registered last in the exception handler pipeline so
/// that more specific handlers (domain, validation, etc.) get first chance.
/// <para>
/// It maps one exception by type before falling back to 500: a
/// <see cref="CrossTenantWriteException"/> is a caller fault, not a server fault, and is answered
/// with <c>400 Bad Request</c>. The mapping lives here rather than in its own handler because the
/// exception derives from <see cref="InvalidOperationException"/>, so nothing ahead of this handler
/// claims it, and every other save-time invariant failure of that family still ends at the 500.
/// </para>
/// </summary>
/// <param name="problemDetailsService">The service used to write RFC 9457 problem details.</param>
/// <param name="logger">Logger for recording unhandled exceptions.</param>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    /// <summary>The problem title reported for a write rejected at the tenant boundary.</summary>
    internal const string CrossTenantWriteTitle = "Tenant write rejected";

    /// <summary>
    /// The problem detail reported for a write rejected at the tenant boundary. Deliberately free of
    /// anything the exception carries: the entity type and both tenant ids are server-side state,
    /// and echoing a tenant id back tells an unauthorized caller which tenant owns the row it just
    /// tried to write. The log entry keeps the full failure for the operator.
    /// </summary>
    internal const string CrossTenantWriteDetail =
        "The request did not carry a tenant that may perform this write. Supply the tenant on the "
        + "request (the configured tenant claim or header) and retry as a tenant you belong to.";

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is CrossTenantWriteException crossTenantWrite)
        {
            // Warning, not error: the request was refused exactly as designed, and a tenant-scoped
            // API answering 400 to an untenanted write is routine rather than a server fault.
            logger.LogWarning(crossTenantWrite, "Save rejected at the tenant boundary");

            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = httpContext.Response.StatusCode,
                    Title = CrossTenantWriteTitle,
                    Detail = CrossTenantWriteDetail
                }
            }).ConfigureAwait(false);
        }

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
        }).ConfigureAwait(false);
    }
}
