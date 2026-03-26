using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Translates FluentValidation's <see cref="ValidationException"/> into an HTTP 400
/// ProblemDetails response. Validation errors are grouped by property name so the client
/// receives a dictionary of field-to-error-messages, matching the standard ASP.NET Core
/// validation error shape.
/// </summary>
/// <param name="problemDetailsService">The service used to write RFC 9457 problem details.</param>
/// <param name="logger">Logger for recording validation exceptions.</param>
public sealed class ValidationExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ValidationExceptionHandler> logger)
    : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
            return false;

        logger.LogWarning(validationException, "Validation exception occurred");

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = httpContext.Response.StatusCode,
                Title = "Validation Exception",
                Detail = "One or more validation errors occurred"
            }
        };

        // Group errors by property so clients get { "PropertyName": ["error1", "error2"] }
        // matching the format produced by ASP.NET Core's built-in model validation.
        var errors = validationException.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray()
            );
        context.ProblemDetails.Extensions.Add("errors", errors);

        return await problemDetailsService.TryWriteAsync(context);
    }
}
