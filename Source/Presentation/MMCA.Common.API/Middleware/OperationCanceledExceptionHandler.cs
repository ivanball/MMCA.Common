using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Handles an <see cref="OperationCanceledException"/> caused by the client disconnecting
/// mid-request (<see cref="HttpContext.RequestAborted"/> is cancelled) by returning HTTP 499 Client
/// Closed Request. Status 499 is a non-standard code (originating from nginx) that signals the
/// client abandoned the request, letting monitoring distinguish cancellations from server errors.
/// A cancellation the client did not cause (a downstream <c>HttpClient</c> timeout, an internal
/// token) is left to the generic handler, which answers 500 and logs it as the server error it is.
/// <para>
/// A <see cref="DbException"/> raised after the client has gone is claimed the same way: a data
/// provider may report the cancelled command as its own exception rather than an
/// <see cref="OperationCanceledException"/> (SqlClient answers "Operation cancelled by user." as a
/// <c>SqlException</c>). Nobody reads that response any more, and the exception is still logged with
/// its stack at Warning, so a database fault that happens to coincide with a disconnect stays visible.
/// Any other exception type is left to the generic handler even after a disconnect.
/// </para>
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
        if (exception is not (OperationCanceledException or DbException)
            || !httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        logger.LogWarning(exception, "Operation canceled — client disconnected");

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

        return await problemDetailsService.TryWriteAsync(context).ConfigureAwait(false);
    }
}
