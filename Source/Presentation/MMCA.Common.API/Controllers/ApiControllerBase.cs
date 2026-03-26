using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    /// Immutable mapping from domain error types to HTTP status codes. Uses <see cref="FrozenDictionary{TKey,TValue}"/>
    /// for optimal read performance since the mapping is fixed at startup.
    /// </summary>
    private static readonly FrozenDictionary<ErrorType, int> ErrorTypeToStatusCode = new Dictionary<ErrorType, int>
    {
        [ErrorType.Validation] = StatusCodes.Status400BadRequest,
        [ErrorType.Invariant] = StatusCodes.Status400BadRequest,
        [ErrorType.NotFound] = StatusCodes.Status404NotFound,
        [ErrorType.Conflict] = StatusCodes.Status409Conflict,
        [ErrorType.Unauthorized] = StatusCodes.Status401Unauthorized,
        [ErrorType.Forbidden] = StatusCodes.Status403Forbidden,
        [ErrorType.UnprocessableEntity] = StatusCodes.Status422UnprocessableEntity,
        [ErrorType.Failure] = StatusCodes.Status400BadRequest,
    }.ToFrozenDictionary();

    /// <summary>
    /// Converts a collection of domain <see cref="Error"/> objects into an RFC 9457 Problem Details response.
    /// The HTTP status code is determined by the first error's type. All errors are included in
    /// the "errors" extension property for client consumption.
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

        // Status code is driven by the first error; callers should put the most significant error first
        var firstError = errorList[0];

        var statusCode = ErrorTypeToStatusCode.GetValueOrDefault(
            firstError.Type,
            StatusCodes.Status400BadRequest);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = "Operation failed",
            Detail = "One or more errors occurred."
        };

        problemDetails.Extensions["errors"] = errorList
            .Select(e => new
            {
                e.Code,
                e.Message,
                Type = e.Type.ToString(),
                e.Source,
                e.Target
            })
            .ToArray();

        return StatusCode(statusCode, problemDetails);
    }
}
