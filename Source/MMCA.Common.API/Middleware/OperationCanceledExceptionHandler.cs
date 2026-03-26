using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Handles <see cref="OperationCanceledException"/> (typically triggered when the client
/// disconnects mid-request) by returning HTTP 499 Client Closed Request. Status 499 is a
/// non-standard code (originating from nginx) that signals the client abandoned the request,
/// letting monitoring distinguish cancellations from server errors.
/// </summary>
/// <param name="problemDetailsService">The service used to write RFC 9457 problem details.</param>
/// <param name="logger">Logger for recording cancellation events.</param>
public sealed class OperationCanceledExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<OperationCanceledExceptionHandler> logger)
    : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not OperationCanceledException operationCanceledException)
            return false;

        logger.LogWarning(operationCanceledException, "Operation canceled — client disconnected");

        httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = httpContext.Response.StatusCode,
                Title = "Operation Canceled Exception",
                Detail = "The operation was canceled by the client"
            }
        };

        return await problemDetailsService.TryWriteAsync(context);
    }
}
