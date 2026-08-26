using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.Localization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Shared mapping from domain <see cref="ErrorType"/> values to HTTP status codes and RFC 9457
/// Problem Details responses. Centralizes the error-to-HTTP translation so that
/// <see cref="Controllers.ApiControllerBase"/> and <see cref="UnhandledResultFailureFilter"/>
/// stay consistent without duplicating the mapping dictionary.
/// </summary>
internal static class ErrorHttpMapping
{
    /// <summary>
    /// Immutable mapping from domain error types to HTTP status codes. Uses <see cref="FrozenDictionary{TKey,TValue}"/>
    /// for optimal read performance since the mapping is fixed at startup.
    /// </summary>
    internal static readonly FrozenDictionary<ErrorType, int> ErrorTypeToStatusCode = new Dictionary<ErrorType, int>
    {
        [ErrorType.Validation] = StatusCodes.Status400BadRequest,
        [ErrorType.Invariant] = StatusCodes.Status400BadRequest,
        [ErrorType.NotFound] = StatusCodes.Status404NotFound,
        [ErrorType.Conflict] = StatusCodes.Status409Conflict,
        [ErrorType.Unauthorized] = StatusCodes.Status401Unauthorized,
        [ErrorType.Forbidden] = StatusCodes.Status403Forbidden,
        [ErrorType.UnprocessableEntity] = StatusCodes.Status422UnprocessableEntity,
        [ErrorType.Failure] = StatusCodes.Status400BadRequest,
        [ErrorType.Unexpected] = StatusCodes.Status500InternalServerError,
    }.ToFrozenDictionary();

    /// <summary>
    /// Severity rank per <see cref="ErrorType"/>, highest wins. A <see cref="Result"/> built by
    /// <see cref="Result.Combine"/> aggregates errors in evaluation order, so picking the first
    /// error's status would let an incidental validation failure downgrade a real 403 or 500 to a
    /// 400. Ranking the categories instead makes the aggregate status independent of ordering.
    /// The ranking, most to least severe:
    /// <list type="number">
    ///   <item><see cref="ErrorType.Unexpected"/> (500): the server itself is broken.</item>
    ///   <item><see cref="ErrorType.Unauthorized"/> (401): the caller has not proven who they are, so nothing else can be judged.</item>
    ///   <item><see cref="ErrorType.Forbidden"/> (403): the caller is known but not allowed.</item>
    ///   <item><see cref="ErrorType.Conflict"/> (409): the request lost a race with the current state.</item>
    ///   <item><see cref="ErrorType.NotFound"/> (404): the target does not exist.</item>
    ///   <item><see cref="ErrorType.UnprocessableEntity"/> (422): well-formed but semantically rejected.</item>
    ///   <item><see cref="ErrorType.Invariant"/>, <see cref="ErrorType.Validation"/>, <see cref="ErrorType.Failure"/> (400): the caller can fix the request.</item>
    /// </list>
    /// </summary>
    private static readonly FrozenDictionary<ErrorType, int> ErrorTypeSeverityRank = new Dictionary<ErrorType, int>
    {
        [ErrorType.Unexpected] = 70,
        [ErrorType.Unauthorized] = 60,
        [ErrorType.Forbidden] = 50,
        [ErrorType.Conflict] = 40,
        [ErrorType.NotFound] = 30,
        [ErrorType.UnprocessableEntity] = 20,
        [ErrorType.Invariant] = 10,
        [ErrorType.Validation] = 10,
        [ErrorType.Failure] = 10,
    }.ToFrozenDictionary();

    /// <summary>
    /// Resolves the HTTP status code for the given error type, falling back to 400 Bad Request
    /// if the error type is not explicitly mapped.
    /// </summary>
    internal static int GetStatusCode(ErrorType errorType) =>
        ErrorTypeToStatusCode.GetValueOrDefault(errorType, StatusCodes.Status400BadRequest);

    /// <summary>
    /// Resolves the HTTP status code for a whole failure by taking the most severe
    /// <see cref="ErrorType"/> present, per <see cref="ErrorTypeSeverityRank"/>. Ties keep the
    /// earliest error, so a list of same-rank errors behaves exactly as before. Every error still
    /// travels in the Problem Details "errors" array; only the status is ranked.
    /// </summary>
    /// <param name="errors">The errors carried by the failed result. Must not be empty.</param>
    /// <returns>The HTTP status code of the highest-ranked error type present.</returns>
    internal static int GetStatusCode(IReadOnlyList<Error> errors)
    {
        var mostSevere = errors[0].Type;
        var highestRank = GetSeverityRank(mostSevere);

        // Deliberately index-based: this runs on every failure response, and a LINQ
        // MaxBy would allocate an enumerator plus a comparer closure per call.
        for (var i = 1; i < errors.Count; i++)
        {
            var rank = GetSeverityRank(errors[i].Type);
            if (rank > highestRank)
            {
                highestRank = rank;
                mostSevere = errors[i].Type;
            }
        }

        return GetStatusCode(mostSevere);
    }

    /// <summary>
    /// Returns the severity rank of an error type. An unmapped type ranks lowest, so a category
    /// added without a rank can never silently outrank a real 403 or 500.
    /// </summary>
    private static int GetSeverityRank(ErrorType errorType) =>
        ErrorTypeSeverityRank.GetValueOrDefault(errorType, 0);

    /// <summary>
    /// Builds the "errors" extension array used in Problem Details responses. Each error is
    /// projected into an anonymous object with Code, Message, Type, Source, and Target properties.
    /// The human-readable <c>Message</c> is localized at the edge via <paramref name="localizer"/>,
    /// keyed by the stable <c>Code</c> (ADR-027); <c>Code</c>/<c>Type</c>/<c>Source</c>/<c>Target</c>
    /// stay verbatim so clients can still branch on them. A <see langword="null"/> localizer (no
    /// localization registered) leaves the original English <c>Message</c> unchanged.
    /// </summary>
    internal static object[] BuildErrorsExtension(IReadOnlyList<Error> errors, IErrorLocalizer? localizer) =>
        [.. errors.Select(e => new
        {
            e.Code,
            Message = localizer is null ? e.Message : localizer.Localize(e.Code, e.Message),
            Type = e.Type.ToString(),
            e.Source,
            e.Target
        })];
}
