namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Classifies domain errors into categories that map directly to HTTP status codes
/// via <c>ApiControllerBase</c>. The first error in a <see cref="Result"/> determines
/// the response status code.
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
    Failure
}
