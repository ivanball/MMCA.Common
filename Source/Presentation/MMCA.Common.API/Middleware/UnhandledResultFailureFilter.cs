using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// A global action filter that catches controller actions which accidentally return a
/// <see cref="Result"/> or <see cref="Result{T}"/> object directly (e.g., <c>return Ok(result)</c>)
/// when the result represents a failure. Without this filter, such responses would serialize
/// the internal <see cref="Result"/> structure as a 200 OK JSON body — hiding the error.
/// <para>
/// When detected, the filter replaces the response with an RFC 9457 Problem Details response
/// using the appropriate HTTP status code derived from the first error's <see cref="ErrorType"/>.
/// </para>
/// </summary>
public sealed partial class UnhandledResultFailureFilter(
    ILogger<UnhandledResultFailureFilter> logger) : IAlwaysRunResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: Result result } || result.IsSuccess)
        {
            return;
        }

        LogUnhandledResultFailure(context.ActionDescriptor.DisplayName, result.Errors);

        var firstError = result.Errors.Count > 0 ? result.Errors[0] : null;
        var statusCode = firstError is not null
            ? ErrorHttpMapping.GetStatusCode(firstError.Type)
            : StatusCodes.Status500InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = "Unhandled result failure",
            Detail = "The action returned a Result.Failure that was not mapped to an HTTP error response."
        };

        problemDetails.Extensions["errors"] = ErrorHttpMapping.BuildErrorsExtension(result.Errors);

        context.Result = new ObjectResult(problemDetails) { StatusCode = statusCode };
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unhandled Result.Failure detected in action '{ActionName}'. Errors: {Errors}")]
    private static partial void LogUnhandledResultFailure(
        ILogger logger,
        string? actionName,
        IReadOnlyList<Error> errors);

    private void LogUnhandledResultFailure(string? actionName, IReadOnlyList<Error> errors) =>
        LogUnhandledResultFailure(logger, actionName, errors);
}
