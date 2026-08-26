using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Base controller for all API controllers. Provides centralized error-to-HTTP-status mapping
/// using the Result pattern, translating domain <see cref="ErrorType"/> values into RFC 9457
/// Problem Details responses.
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// Converts a collection of domain <see cref="Error"/> objects into an RFC 9457 Problem Details response.
    /// All errors are included in the "errors" extension property for client consumption.
    /// <para>
    /// The HTTP status code is the one belonging to the most severe <see cref="ErrorType"/> present,
    /// not the first error's, so an aggregate built by <see cref="Result.Combine"/> cannot be
    /// downgraded by error ordering: a 403 or 500 travelling alongside a validation error still
    /// answers 403 or 500. Ranking, most to least severe:
    /// <see cref="ErrorType.Unexpected"/> (500) &gt; <see cref="ErrorType.Unauthorized"/> (401) &gt;
    /// <see cref="ErrorType.Forbidden"/> (403) &gt; <see cref="ErrorType.Conflict"/> (409) &gt;
    /// <see cref="ErrorType.NotFound"/> (404) &gt; <see cref="ErrorType.UnprocessableEntity"/> (422) &gt;
    /// <see cref="ErrorType.Invariant"/> / <see cref="ErrorType.Validation"/> /
    /// <see cref="ErrorType.Failure"/> (400). Equal ranks keep the earliest error.
    /// </para>
    /// </summary>
    /// <param name="errors">The domain errors to convert. If null or empty, returns a 500 response.</param>
    /// <returns>An <see cref="ObjectResult"/> containing a <see cref="ProblemDetails"/> payload.</returns>
    protected virtual ObjectResult HandleFailure(IEnumerable<Error> errors)
    {
        var errorList = errors?.ToList();

        if (errorList is null || errorList.Count == 0)
        {
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Unknown error",
                detail: "An unknown error has occurred.");
        }

        // Status code is driven by the most severe error present, never by position.
        var statusCode = ErrorHttpMapping.GetStatusCode(errorList);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = "Operation failed",
            Detail = "One or more errors occurred."
        };

        var localizer = HttpContext.RequestServices?.GetService<IErrorLocalizer>();
        problemDetails.Extensions["errors"] = ErrorHttpMapping.BuildErrorsExtension(errorList, localizer);

        return StatusCode(statusCode, problemDetails);
    }
}
