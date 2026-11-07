using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// The HTTP half of read-only view-as (ADR-137). Runs after authentication and routing, and for a
/// request whose principal carries <c>act_sub</c> (a view-as token):
/// <list type="number">
/// <item>refuses every method other than GET, HEAD and OPTIONS with 403, except the one action marked
/// as the end-view-as endpoint, because several writes never pass through a command (sign-in,
/// refresh, password and preference changes, device registration);</item>
/// <item>checks that the token's grant is still live (exists, names this staff user and this
/// account, not ended, not expired) and refuses it with 401 otherwise, so ending a view takes effect
/// on the next request rather than at token expiry.</item>
/// </list>
/// A request without <c>act_sub</c> passes untouched and pays nothing. Added to the pipeline only
/// while <c>ViewAs:Enabled</c> is true; the command decorator is the second layer either way.
/// </summary>
/// <remarks>
/// <b>The grant check fails CLOSED</b>, unlike <see cref="SoftDeletedUserMiddleware"/>: a store error
/// answers 401. The exposure differs (there every user's every request, here one staff user's support
/// view), so an outage costs a support session, never a write in someone else's name. The lookup is
/// one primary-key read per view-as request and is not cached, because a cached "live" answer on one
/// replica would outlive an End handled on another.
/// </remarks>
/// <param name="next">The next middleware in the pipeline.</param>
public sealed partial class ViewAsGuardMiddleware(RequestDelegate next)
{
    /// <summary>Applies the guard.</summary>
    /// <param name="context">The request.</param>
    /// <param name="logger">Logs refused requests.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <remarks>
    /// <see cref="IViewAsService"/> is resolved from the request's services only for a view-as
    /// request, so an ordinary request resolves nothing; a host where it cannot be resolved refuses
    /// view-as requests (fail closed).
    /// </remarks>
    public async Task InvokeAsync(HttpContext context, ILogger<ViewAsGuardMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);

        var user = context.User;
        if (!user.IsViewAs())
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!IsSafeMethod(context.Request.Method) && !IsEndViewAsEndpoint(context))
        {
            LogWriteRefused(logger, context.Request.Method);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var viewAs = context.RequestServices?.GetService<IViewAsService>();
        if (viewAs is null || !await IsGrantLiveAsync(user, viewAs, logger, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsEndViewAsEndpoint(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.GetEndpoint()?.Metadata.GetMetadata<ViewAsEndEndpointAttribute>() is not null;

    private static async Task<bool> IsGrantLiveAsync(
        ClaimsPrincipal user,
        IViewAsService viewAs,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(user.FindFirst(AuthClaimTypes.ViewAsGrant)?.Value, out var grantId)
            || !UserIdentifierType.TryParse(user.FindFirst(AuthClaimTypes.ViewAsActor)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var staffUserId)
            || user.GetUserId() is not { } targetUserId)
        {
            LogMalformedToken(logger);
            return false;
        }

        try
        {
            return await viewAs.IsGrantActiveAsync(grantId, staffUserId, targetUserId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGrantLookupFailed(logger, grantId, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "View-as request refused: {Method} is not allowed under a read-only view-as token")]
    private static partial void LogWriteRefused(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "View-as request refused: the token carries no usable grant, staff or subject claim")]
    private static partial void LogMalformedToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "View-as grant {GrantId} could not be checked; the request is refused (fail closed)")]
    private static partial void LogGrantLookupFailed(ILogger logger, Guid grantId, Exception exception);
}

/// <summary>
/// Marks the one action a view-as token may call with a non-safe method: ending the view. Read by
/// <see cref="ViewAsGuardMiddleware"/> from the routed endpoint's metadata, so the exemption follows
/// the action wherever the host routes its auth controller. Internal on purpose: no other write is
/// ever allowed under a view-as token.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
internal sealed class ViewAsEndEndpointAttribute : Attribute;
