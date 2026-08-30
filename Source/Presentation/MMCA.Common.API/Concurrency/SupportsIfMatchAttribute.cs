using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Concurrency;

/// <summary>
/// Makes a write action conditional: the optimistic-concurrency token comes from the HTTP
/// <c>If-Match</c> header, the header is mandatory, and a failed precondition is answered with 412
/// rather than 409.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> Before the action runs, the <c>If-Match</c> header is decoded (see
/// <see cref="ConcurrencyETag"/>) and placed in <see cref="HttpContext.Items"/> under
/// <see cref="TokenItemKey"/>, where the action reads it. After the action runs, a conflict outcome
/// is rewritten to <c>412 Precondition Failed</c>, keeping the response body exactly as it was built.
/// </para>
/// <para>
/// <b>The header is required.</b> A guarded mutation without a usable token would be a
/// last-write-wins write, so a request that states no precondition is refused with
/// <c>428 Precondition Required</c> and the action never runs. A malformed tag is a
/// <c>400 Bad Request</c>: the server cannot tell what the caller meant. <c>*</c> counts as no
/// precondition, because it names no particular version.
/// </para>
/// <para>
/// <b>Why 412 and not 409.</b> RFC 9110 reserves 412 for a precondition the client stated in a
/// conditional request header, which is precisely what <c>If-Match</c> is; 409 stays the answer for a
/// conflict the client did not condition on. Note that the rewrite keys on the conflict OUTCOME (a
/// 409 result, or the EF Core concurrency exception), because this framework surfaces a stale row
/// version through the generic <c>Conflict</c> channel rather than a dedicated error code; an
/// endpoint whose 409 means something else (a duplicate key, say) reports that conflict as 412, with
/// the original problem details, including the error codes, intact.
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
    /// <see cref="HttpContext.Items"/> key holding the decoded <c>If-Match</c> token (a
    /// <see cref="byte"/> array) for the action about to run. The token travels here rather than in
    /// the request model, so a request body never carries a concurrency token and no model has to
    /// loosen its immutability to receive one.
    /// </summary>
    public const string TokenItemKey = "MMCA.Common.API.Concurrency.IfMatchToken";

    /// <summary>
    /// Reads the token the filter decoded for the current request.
    /// </summary>
    /// <param name="httpContext">The request whose <c>If-Match</c> token is wanted.</param>
    /// <returns>The decoded token.</returns>
    /// <exception cref="InvalidOperationException">
    /// The action was reached without this filter, so no token was decoded. That is a wiring
    /// mistake, not a client error: every conditional action carries <c>[SupportsIfMatch]</c>.
    /// </exception>
    public static byte[] RequiredToken(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Items.TryGetValue(TokenItemKey, out var value) && value is byte[] rowVersion
            ? rowVersion
            : throw new InvalidOperationException(
                $"No If-Match token was decoded for this request. Apply [{nameof(SupportsIfMatchAttribute)}] to the action that reads it.");
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!TryApplyIfMatch(context))
        {
            // The 428 or the 400 is already on the context and the action must not run.
            return;
        }

        var executed = await next().ConfigureAwait(false);

        RewriteConflictToPreconditionFailed(executed);
    }

    /// <summary>
    /// Decodes the <c>If-Match</c> header into <see cref="HttpContext.Items"/>.
    /// </summary>
    /// <param name="context">The executing action context.</param>
    /// <returns>
    /// <see langword="true"/> when a token was decoded and the action may run; <see langword="false"/>
    /// when the request has been short-circuited with 428 (no precondition stated) or 400 (a
    /// precondition the server cannot read).
    /// </returns>
    private static bool TryApplyIfMatch(ActionExecutingContext context)
    {
        context.HttpContext.Request.Headers.TryGetValue(ConcurrencyETag.IfMatchHeaderName, out var headerValues);
        var headerValue = headerValues.ToString();

        if (string.IsNullOrWhiteSpace(headerValue)
            || string.Equals(headerValue.Trim(), ConcurrencyETag.Wildcard, StringComparison.Ordinal))
        {
            context.Result = PreconditionRequiredResult(context.HttpContext);
            return false;
        }

        if (!ConcurrencyETag.TryParse(headerValue, out var rowVersion))
        {
            context.Result = MalformedIfMatchResult(context.HttpContext);
            return false;
        }

        context.HttpContext.Items[TokenItemKey] = rowVersion;
        return true;
    }

    /// <summary>
    /// Rewrites a conflict outcome to 412: every request reaching the action stated a precondition.
    /// </summary>
    /// <param name="executed">The executed action context.</param>
    private static void RewriteConflictToPreconditionFailed(ActionExecutedContext executed)
    {
        if (executed.Exception is DbUpdateConcurrencyException)
        {
            // The global handler would map this to 409 like any other DbUpdateException. Under an
            // explicit precondition it is a failed precondition, so it is answered here instead.
            executed.Result = PreconditionFailedResult(executed.HttpContext);
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

    /// <summary>The response for a conditional write that stated no precondition at all.</summary>
    private static ObjectResult PreconditionRequiredResult(HttpContext httpContext) =>
        Problem(
            httpContext,
            StatusCodes.Status428PreconditionRequired,
            "If-Match header required",
            "This write is conditional. Send the entity tag from your last read in the If-Match header, for example W/\"AAAAAAAAB9E=\".",
            Error.Validation(
                "Concurrency.PreconditionRequired",
                "This write is conditional; the If-Match header is required.",
                nameof(SupportsIfMatchAttribute)));

    /// <summary>The response for an <c>If-Match</c> value that is not a decodable entity tag.</summary>
    private static ObjectResult MalformedIfMatchResult(HttpContext httpContext) =>
        Problem(
            httpContext,
            StatusCodes.Status400BadRequest,
            "Invalid If-Match header",
            "The If-Match header must be an entity tag returned by a previous read, for example W/\"AAAAAAAAB9E=\".",
            Error.Validation(
                "Concurrency.MalformedIfMatch",
                "The If-Match header is not a decodable entity tag.",
                nameof(SupportsIfMatchAttribute)));

    /// <summary>The response for a concurrency conflict on a request that stated a precondition.</summary>
    private static ObjectResult PreconditionFailedResult(HttpContext httpContext) =>
        Problem(
            httpContext,
            StatusCodes.Status412PreconditionFailed,
            "Precondition failed",
            "The resource changed since the version named in the If-Match header. Re-read it and retry.",
            Error.Conflict(
                "Concurrency.PreconditionFailed",
                "The resource changed since the version named in the If-Match header.",
                nameof(SupportsIfMatchAttribute)));

    /// <summary>
    /// Builds the problem response the same way the rest of the API surface does: through the
    /// registered <see cref="ProblemDetailsFactory"/> (which stamps the diagnostic extensions such
    /// as <c>traceId</c>), with the error carried in the standard <c>errors</c> extension. The
    /// factory can be absent only in a host that never called the framework's API registration; the
    /// response then still carries the <c>errors</c> extension.
    /// </summary>
    private static ObjectResult Problem(HttpContext httpContext, int statusCode, string title, string detail, Error error)
    {
        var services = httpContext.RequestServices;
        var problemDetails = services?.GetService<ProblemDetailsFactory>()
                ?.CreateProblemDetails(httpContext, statusCode, title: title, detail: detail)
            ?? new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
            };

        problemDetails.Extensions["errors"] =
            ErrorHttpMapping.BuildErrorsExtension([error], services?.GetService<IErrorLocalizer>());

        return new ObjectResult(problemDetails) { StatusCode = statusCode };
    }
}
