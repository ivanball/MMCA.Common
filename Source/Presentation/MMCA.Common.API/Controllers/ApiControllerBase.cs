using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Base controller for the entity and use-case API controllers (the OAuth and service-info bases
/// derive <see cref="ControllerBase"/> directly). Provides centralized error-to-HTTP-status mapping
/// using the Result pattern, translating domain <see cref="ErrorType"/> values into RFC 9457
/// Problem Details responses.
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// The public instance readable <c>byte[] RowVersion</c> property of each runtime type a served row
    /// has had, or null for a type with none. Resolved once per type: a type's shape cannot change at
    /// runtime, so <see cref="SetConcurrencyETag"/> reflects over each type exactly once.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> RowVersionProperties = new();

    /// <summary>
    /// Converts a collection of domain <see cref="Error"/> objects into an RFC 9457 Problem Details response.
    /// All errors are included in the "errors" extension property for client consumption.
    /// <para>
    /// The HTTP status code is the one belonging to the most severe <see cref="ErrorType"/> present,
    /// not the first error's, so an aggregate built by <see cref="Result.Combine"/> cannot be
    /// downgraded by error ordering: a 403 or 500 travelling alongside a validation error still
    /// answers 403 or 500. Ranking, most to least severe:
    /// <see cref="ErrorType.Unexpected"/> (500) &gt; <see cref="ErrorType.Unauthorized"/> (401) &gt;
    /// <see cref="ErrorType.Forbidden"/> (403) &gt; <see cref="ErrorType.TooManyRequests"/> (429) &gt;
    /// <see cref="ErrorType.Conflict"/> (409) &gt; <see cref="ErrorType.NotFound"/> (404) &gt; <see cref="ErrorType.UnprocessableEntity"/> (422) &gt;
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

    /// <summary>
    /// Emits a served row's concurrency token as a weak <c>ETag</c>, so a client can hand it straight
    /// back as an <c>If-Match</c> precondition on the next write (see <see cref="SupportsIfMatchAttribute"/>),
    /// which is the only route a token travels.
    /// </summary>
    /// <param name="dto">The row just served, or null: a typed value on an ordinary read, or a shaped
    /// dictionary keyed by JSON names when the caller asked for a field projection.</param>
    /// <remarks>
    /// <para>
    /// The token is read off the row's RUNTIME shape: a public instance <c>byte[]</c> property named
    /// exactly <c>RowVersion</c> on a typed value, or the <c>rowVersion</c> (else <c>RowVersion</c>)
    /// entry of an <see cref="IDictionary{TKey, TValue}"/> row. A row without a token, or with an
    /// empty one, gets no header: absent is the correct answer for a resource that has no version to
    /// condition on, and a fabricated tag would invite preconditions the write side cannot honour.
    /// </para>
    /// <para>
    /// Protected so any controller that serves a row (a custom read action, a projection built by
    /// hand, a conditional write answering with the refreshed value) emits the same header instead of
    /// re-implementing the reflection and the base64 format, which is where the two drift apart and an
    /// <c>If-Match</c> precondition quietly stops working.
    /// </para>
    /// </remarks>
    protected void SetConcurrencyETag(object? dto)
    {
        if (dto is null)
            return;

        if (ReadRowVersion(dto) is not { Length: > 0 } rowVersion)
            return;

        Response.Headers[ConcurrencyETag.ETagHeaderName] = ConcurrencyETag.Format(rowVersion);
    }

    /// <summary>
    /// Runs a conditional write that answers <c>204 No Content</c> on success: reads the <c>If-Match</c>
    /// token <see cref="SupportsIfMatchAttribute"/> decoded, builds the command from it, dispatches the
    /// command, and awaits <paramref name="onSuccess"/> before answering.
    /// </summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <typeparam name="TResult">The handler's result type: <see cref="Result"/> or any <see cref="Result{T}"/>,
    /// whose value is discarded.</typeparam>
    /// <param name="handler">The command handler (the decorated pipeline the controller was given).</param>
    /// <param name="buildCommand">Builds the command from the decoded concurrency token.</param>
    /// <param name="onSuccess">Optional follow-up awaited after a successful write and before the 204,
    /// for example a cache eviction. Not called on failure; an exception it throws propagates.</param>
    /// <param name="cancellationToken">Cancellation token passed to the handler and to <paramref name="onSuccess"/>.</param>
    /// <returns>204 on success, or the Problem Details <see cref="HandleFailure"/> builds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> or <paramref name="buildCommand"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The action carries no <see cref="SupportsIfMatchAttribute"/>,
    /// so no token was decoded; the handler never runs.</exception>
    protected Task<ActionResult> HandleConditionalCommandAsync<TCommand, TResult>(
        ICommandHandler<TCommand, TResult> handler,
        Func<byte[], TCommand> buildCommand,
        Func<CancellationToken, ValueTask>? onSuccess = null,
        CancellationToken cancellationToken = default)
        where TResult : Result
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(buildCommand);

        var rowVersion = SupportsIfMatchAttribute.RequiredToken(HttpContext);
        return RunConditionalCommandAsync(handler, buildCommand(rowVersion), onSuccess, cancellationToken);
    }

    /// <summary>
    /// Runs a conditional write that answers <c>200 OK</c> with the handler's value on success: reads the
    /// <c>If-Match</c> token <see cref="SupportsIfMatchAttribute"/> decoded, builds the command from it,
    /// dispatches the command, awaits <paramref name="onSuccess"/>, emits the value's concurrency token
    /// as an <c>ETag</c> (see <see cref="SetConcurrencyETag"/>) and answers with the value.
    /// </summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <typeparam name="TResponse">The value the handler returns and the response carries.</typeparam>
    /// <param name="handler">The command handler (the decorated pipeline the controller was given).</param>
    /// <param name="buildCommand">Builds the command from the decoded concurrency token.</param>
    /// <param name="onSuccess">Optional follow-up awaited with the value after a successful write and
    /// before the 200. Not called on failure; an exception it throws propagates.</param>
    /// <param name="cancellationToken">Cancellation token passed to the handler and to <paramref name="onSuccess"/>.</param>
    /// <returns>200 with the value on success, or the Problem Details <see cref="HandleFailure"/> builds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> or <paramref name="buildCommand"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The action carries no <see cref="SupportsIfMatchAttribute"/>,
    /// so no token was decoded; the handler never runs.</exception>
    protected Task<ActionResult<TResponse>> HandleConditionalCommandWithResponseAsync<TCommand, TResponse>(
        ICommandHandler<TCommand, Result<TResponse>> handler,
        Func<byte[], TCommand> buildCommand,
        Func<TResponse, CancellationToken, ValueTask>? onSuccess = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(buildCommand);

        var rowVersion = SupportsIfMatchAttribute.RequiredToken(HttpContext);
        return RunConditionalCommandWithResponseAsync(handler, buildCommand(rowVersion), onSuccess, cancellationToken);
    }

    /// <summary>The awaited half of <see cref="HandleConditionalCommandAsync"/>, after the eager argument checks.</summary>
    private async Task<ActionResult> RunConditionalCommandAsync<TCommand, TResult>(
        ICommandHandler<TCommand, TResult> handler,
        TCommand command,
        Func<CancellationToken, ValueTask>? onSuccess,
        CancellationToken cancellationToken)
        where TResult : Result
    {
        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return HandleFailure(result.Errors);

        if (onSuccess is not null)
            await onSuccess(cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>The awaited half of <see cref="HandleConditionalCommandWithResponseAsync"/>, after the eager argument checks.</summary>
    private async Task<ActionResult<TResponse>> RunConditionalCommandWithResponseAsync<TCommand, TResponse>(
        ICommandHandler<TCommand, Result<TResponse>> handler,
        TCommand command,
        Func<TResponse, CancellationToken, ValueTask>? onSuccess,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetValue(out var value))
            return HandleFailure(result.Errors);

        if (onSuccess is not null)
            await onSuccess(value, cancellationToken).ConfigureAwait(false);

        SetConcurrencyETag(value);
        return Ok(value);
    }

    /// <summary>
    /// Reads the concurrency token off a served row: the <c>rowVersion</c> (else <c>RowVersion</c>)
    /// entry of a shaped dictionary, or the typed value's <c>RowVersion</c> property.
    /// </summary>
    /// <param name="dto">The row just served.</param>
    /// <returns>The token, or null when this row carries none.</returns>
    private static byte[]? ReadRowVersion(object dto)
    {
        if (dto is IDictionary<string, object?> shaped)
        {
            return shaped.TryGetValue("rowVersion", out var value) || shaped.TryGetValue("RowVersion", out value)
                ? value as byte[]
                : null;
        }

        var property = RowVersionProperties.GetOrAdd(dto.GetType(), FindRowVersionProperty);
        return property?.GetValue(dto) as byte[];
    }

    /// <summary>Finds a type's public instance readable <c>byte[]</c> property named exactly <c>RowVersion</c>.</summary>
    /// <param name="type">The runtime type of a served row.</param>
    /// <returns>The property, or null when the type has none.</returns>
    private static PropertyInfo? FindRowVersionProperty(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.CanRead
                && p.GetIndexParameters().Length == 0
                && p.PropertyType == typeof(byte[])
                && string.Equals(p.Name, "RowVersion", StringComparison.Ordinal));
}
