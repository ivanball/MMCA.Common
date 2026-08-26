namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Classifies domain errors into categories that map directly to HTTP status codes
/// via <c>ApiControllerBase</c>. When a <see cref="Result"/> carries several errors
/// (typically from <see cref="Result.Combine"/>), the response status code is the one
/// belonging to the highest-ranked category present, so an aggregate can never be
/// downgraded by the ordering of its errors.
/// </summary>
public enum ErrorType
{
    /// <summary>Input/request validation failure (HTTP 400).</summary>
    Validation,

    /// <summary>Domain invariant violation — a business rule was broken (HTTP 400).</summary>
    Invariant,

    /// <summary>Requested entity does not exist (HTTP 404).</summary>
    NotFound,

    /// <summary>Operation conflicts with current state, e.g. duplicate or already deleted (HTTP 409).</summary>
    Conflict,

    /// <summary>Caller is not authenticated (HTTP 401).</summary>
    Unauthorized,

    /// <summary>Caller is authenticated but lacks permission (HTTP 403).</summary>
    Forbidden,

    /// <summary>Request is well-formed but semantically invalid — e.g. immutable field change attempt (HTTP 422).</summary>
    UnprocessableEntity,

    /// <summary>General/unclassified failure (HTTP 400).</summary>
    Failure,

    /// <summary>
    /// Genuine server-side fault: the request was well-formed and permitted, but the server
    /// could not complete it (HTTP 500). Reserve this for faults the caller cannot fix by
    /// changing the request, and never for business rule violations.
    /// </summary>
    Unexpected
}
