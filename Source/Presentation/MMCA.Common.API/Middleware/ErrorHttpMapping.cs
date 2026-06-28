using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    }.ToFrozenDictionary();

    /// <summary>
    /// Resolves the HTTP status code for the given error type, falling back to 400 Bad Request
    /// if the error type is not explicitly mapped.
    /// </summary>
    internal static int GetStatusCode(ErrorType errorType) =>
        ErrorTypeToStatusCode.GetValueOrDefault(errorType, StatusCodes.Status400BadRequest);

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
