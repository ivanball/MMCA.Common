using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Concurrency;

/// <summary>
/// Lets a write action take its optimistic-concurrency token from the HTTP <c>If-Match</c> header
/// instead of the request body, and answers a failed precondition with 412 rather than 409.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> Before the action runs, an <c>If-Match</c> header is decoded (see
/// <see cref="ConcurrencyETag"/>) and written into every bound argument that implements
/// <see cref="IConcurrencyAware"/> and does not already carry a token. After the action runs, a
/// conflict outcome is rewritten to <c>412 Precondition Failed</c>, keeping the response body exactly
/// as it was built.
/// </para>
/// <para>
/// <b>Body precedence.</b> An argument that already carries a <c>RowVersion</c> is left alone, and
/// the header is then NOT the source of the version, so that request keeps its existing
/// <c>409 Conflict</c> semantics untouched. The two mechanisms therefore coexist: an older client
/// posting the token in the body sees no change at all, and only a caller who actually used the
/// HTTP precondition gets the HTTP precondition status back.
/// </para>
/// <para>
/// <b>Why 412 and not 409.</b> RFC 9110 reserves 412 for a precondition the client stated in a
/// conditional request header, which is precisely what <c>If-Match</c> is; 409 stays the answer for a
/// conflict the client did not condition on. Rewriting on the header-sourced path only is what keeps
/// both meanings intact. Note that the rewrite keys on the conflict OUTCOME (a 409 result, or the EF
/// Core concurrency exception), because this framework surfaces a stale row version through the
/// generic <c>Conflict</c> channel rather than a dedicated error code; a caller who sends
/// <c>If-Match</c> on an endpoint whose 409 means something else (a duplicate key, say) will see that
/// conflict reported as 412, with the original problem details, including the error codes, intact.
/// </para>
/// <para>
/// <b>Attribute IS the filter.</b> Unlike <see cref="Idempotency.IdempotentAttribute"/> this needs no
/// scoped service, so it implements <see cref="IAsyncActionFilter"/> directly and requires no DI
/// registration by the host.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SupportsIfMatchAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key set to <see langword="true"/> when the concurrency token
    /// in play came from the <c>If-Match</c> header rather than the request body. Exposed so an
    /// action (or a downstream filter) can tell the two sources apart.
    /// </summary>
    public const string HeaderSourcedItemKey = "MMCA.Common.API.Concurrency.IfMatchApplied";

    /// <summary>
    /// The <c>RowVersion</c> setter per concrete argument type. Records declare the property
    /// <c>init</c>-only, which the immutability fitness rules require and which reflection can still
    /// invoke, so no request model has to loosen its contract to support conditional writes.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> RowVersionSetters = new();

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var headerSourced = TryApplyIfMatch(context);
        if (headerSourced is null)
        {
            // Malformed header: the 400 is already on the context and the action must not run.
            return;
        }

        var executed = await next().ConfigureAwait(false);

        if (headerSourced == true)
        {
            RewriteConflictToPreconditionFailed(executed);
        }
    }

    /// <summary>
    /// Decodes the <c>If-Match</c> header and populates the bound arguments that want a concurrency
    /// token.
    /// </summary>
    /// <param name="context">The executing action context.</param>
    /// <returns>
    /// <see langword="true"/> when at least one argument took its token from the header,
    /// <see langword="false"/> when the header was absent, a wildcard, or superseded by a token the
    /// body already carried, and <see langword="null"/> when the header was malformed and the request
    /// has been short-circuited with 400.
    /// </returns>
    private static bool? TryApplyIfMatch(ActionExecutingContext context)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(ConcurrencyETag.IfMatchHeaderName, out var headerValues))
        {
            return false;
        }

        var headerValue = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        // "*" means "any current version", which is the same precondition as sending no token at all.
        if (string.Equals(headerValue.Trim(), ConcurrencyETag.Wildcard, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ConcurrencyETag.TryParse(headerValue, out var rowVersion))
        {
            context.Result = MalformedIfMatchResult();
            return null;
        }

        var targets = context.ActionArguments.Values
            .OfType<IConcurrencyAware>()
            .Where(CanTakeRowVersion)
            .ToList();

        foreach (var target in targets)
        {
            RowVersionSetter(target)?.SetValue(target, rowVersion);
        }

        if (targets.Count > 0)
        {
            context.HttpContext.Items[HeaderSourcedItemKey] = true;
        }

        return targets.Count > 0;
    }

    /// <summary>
    /// Whether one bound argument should take its token from the header: it is concurrency-aware,
    /// carries no token of its own, and has a settable <c>RowVersion</c>.
    /// </summary>
    /// <param name="argument">The bound action argument.</param>
    /// <returns><see langword="true"/> when the argument will be populated.</returns>
    /// <remarks>
    /// Value types are skipped: the argument reaching this filter is a boxed copy, so writing to it
    /// would update a box the action never sees. Every request model in this framework is a record
    /// class, so the exclusion costs nothing and avoids a silently ineffective write. A null OR empty
    /// <c>RowVersion</c> counts as "no token supplied", matching what the repository treats as
    /// "skip the conflict check".
    /// </remarks>
    private static bool CanTakeRowVersion(IConcurrencyAware argument) =>
        !argument.GetType().IsValueType
        && argument.RowVersion is not { Length: > 0 }
        && RowVersionSetter(argument) is not null;

    /// <summary>The cached <c>RowVersion</c> setter for one argument's concrete type.</summary>
    /// <param name="argument">The bound action argument.</param>
    /// <returns>The property, or null when the type has no settable <c>RowVersion</c>.</returns>
    private static PropertyInfo? RowVersionSetter(IConcurrencyAware argument) =>
        RowVersionSetters.GetOrAdd(argument.GetType(), ResolveRowVersionSetter);

    /// <summary>Finds the writable <c>RowVersion</c> property on a concrete request type.</summary>
    /// <param name="type">The runtime type of the bound argument.</param>
    /// <returns>The property, or null when it is not settable even by reflection.</returns>
    private static PropertyInfo? ResolveRowVersionSetter(Type type)
    {
        var property = type.GetProperty(
            nameof(IConcurrencyAware.RowVersion),
            BindingFlags.Public | BindingFlags.Instance);

        return property?.SetMethod is not null ? property : null;
    }

    /// <summary>
    /// Rewrites a conflict outcome to 412 for a request whose token came from <c>If-Match</c>.
    /// </summary>
    /// <param name="executed">The executed action context.</param>
    private static void RewriteConflictToPreconditionFailed(ActionExecutedContext executed)
    {
        if (executed.Exception is DbUpdateConcurrencyException)
        {
            // The global handler would map this to 409 like any other DbUpdateException. Under an
            // explicit precondition it is a failed precondition, so it is answered here instead.
            executed.Result = PreconditionFailedResult();
            executed.ExceptionHandled = true;
            return;
        }

        switch (executed.Result)
        {
            case ObjectResult { StatusCode: StatusCodes.Status409Conflict } objectResult:
                objectResult.StatusCode = StatusCodes.Status412PreconditionFailed;
                if (objectResult.Value is ProblemDetails problemDetails)
                {
                    problemDetails.Status = StatusCodes.Status412PreconditionFailed;
                }

                break;

            case StatusCodeResult { StatusCode: StatusCodes.Status409Conflict }:
                executed.Result = new StatusCodeResult(StatusCodes.Status412PreconditionFailed);
                break;

            default:
                break;
        }
    }

    /// <summary>The response for an <c>If-Match</c> value that is not a decodable entity tag.</summary>
    private static ObjectResult MalformedIfMatchResult() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid If-Match header",
            Detail = "The If-Match header must be an entity tag returned by a previous read, for example W/\"AAAAAAAAB9E=\".",
        })
        {
            StatusCode = StatusCodes.Status400BadRequest,
        };

    /// <summary>The response for a concurrency conflict on a request that stated a precondition.</summary>
    private static ObjectResult PreconditionFailedResult() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status412PreconditionFailed,
            Title = "Precondition failed",
            Detail = "The resource changed since the version named in the If-Match header. Re-read it and retry.",
        })
        {
            StatusCode = StatusCodes.Status412PreconditionFailed,
        };
}
