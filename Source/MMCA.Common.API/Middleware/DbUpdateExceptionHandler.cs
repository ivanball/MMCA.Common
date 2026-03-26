using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Translates EF Core <see cref="DbUpdateException"/> into an HTTP 409 Conflict
/// ProblemDetails response. This covers concurrency conflicts, unique-constraint
/// violations, and foreign-key failures. The inner exception message is included
/// in the detail because it typically contains the database-level constraint name.
/// </summary>
/// <param name="problemDetailsService">The service used to write RFC 9457 problem details.</param>
/// <param name="logger">Logger for recording database update exceptions.</param>
public sealed class DbUpdateExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DbUpdateExceptionHandler> logger)
    : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateException dbUpdateException)
            return false;

        logger.LogError(dbUpdateException, "Unhandled database update exception occurred");

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        // Return a generic message to the client to avoid leaking database schema information.
        // The full exception details are already captured by the LogError call above.
        var detail = "A data conflict occurred. Please retry or contact support.";

        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = httpContext.Response.StatusCode,
                Title = "Database Update Exception",
                Detail = detail
            }
        };

        return await problemDetailsService.TryWriteAsync(context);
    }
}
