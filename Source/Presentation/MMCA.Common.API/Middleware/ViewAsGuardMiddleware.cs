using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// The HTTP half of read-only view-as (ADR-137). Runs after authentication and routing, and for a
/// request whose principal carries <c>act_sub</c> (a view-as token):
/// <list type="number">
/// <item>refuses every method other than GET, HEAD and OPTIONS with 403, except the one action marked
/// as the end-view-as endpoint and an endpoint a host marked
/// <see cref="ViewAsReadOnlyEndpointAttribute"/> (gRPC is always POST), because several writes never
/// pass through a command (sign-in, refresh, password and preference changes, device
/// registration);</item>
/// <item>checks that the token's grant is still live (exists, names this staff user and this
/// account, not ended, not expired) and refuses it with 401 otherwise, so ending a view takes effect
/// on the next request rather than at token expiry. On a host with <c>ViewAs:HostsGrantStore</c>
/// (the default) that is a read of the grant row; on a service of a multi-service host that does not
/// hold the store, it is the token's own expiry plus the shared cache's grant-ended marker
/// (<see cref="ViewAsEndedMarker"/>), which the store host's end endpoint writes.</item>
/// </list>
/// A request without <c>act_sub</c> passes untouched and pays one claim lookup. Always in the
/// pipeline: while <c>ViewAs:Enabled</c> is false every view-as request is refused 401, so a token
/// minted before view-as was switched off cannot reach a write that bypasses commands. The command
/// decorator is the second layer either way.
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

        // View-as switched off (or never configured on this host): a still-valid view-as token minted
        // while it was on is honored for nothing, so no write path can see it.
        var settings = context.RequestServices?.GetService<IOptions<ViewAsSettings>>()?.Value;
        if (settings is null || !settings.Enabled)
        {
            LogViewAsDisabled(logger);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!IsSafeMethod(context.Request.Method) && !IsEndViewAsEndpoint(context) && !IsReadOnlyEndpoint(context))
        {
            LogWriteRefused(logger, context.Request.Method);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!await IsGrantLiveOnThisHostAsync(context, settings.HostsGrantStore, logger).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The grant check: a read of the grant row on the host that holds the store, the token's expiry and
    /// the shared-cache marker on a service that does not.
    /// </summary>
    private static async Task<bool> IsGrantLiveOnThisHostAsync(HttpContext context, bool hostsGrantStore, ILogger logger)
    {
        if (!hostsGrantStore)
        {
            return await IsGrantLiveWithoutTheStoreAsync(context, context.User, logger).ConfigureAwait(false);
        }

        var viewAs = context.RequestServices.GetService<IViewAsService>();
        return viewAs is not null
            && await IsGrantLiveAsync(context.User, viewAs, logger, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsEndViewAsEndpoint(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.GetEndpoint()?.Metadata.GetMetadata<ViewAsEndEndpointAttribute>() is not null;

    /// <summary>
    /// Whether this is a POST to an endpoint the host marked read-only (gRPC is always POST). Exempts
    /// the METHOD rule alone: the Enabled check above and the grant check after it still run.
    /// </summary>
    private static bool IsReadOnlyEndpoint(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.GetEndpoint()?.Metadata.GetMetadata<ViewAsReadOnlyEndpointAttribute>() is not null;

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

    /// <summary>
    /// The check on a service that does not hold the grant store (a multi-service host's non-Identity
    /// services): the token must carry its grant, staff and subject claims, must not be past its own
    /// expiry (the grant's expiry, which the store host would check), and the shared cache must hold no
    /// grant-ended marker for it (<see cref="ViewAsEndedMarker"/>).
    /// </summary>
    /// <remarks>
    /// <b>Fails CLOSED when the cache cannot be read</b> (an exception, or no cache registered), unlike
    /// <see cref="SoftDeletedUserMiddleware"/>, which fails open on the same outage. Here the marker is
    /// the ONLY evidence that a view was ended, so an unreadable cache would quietly turn every ended
    /// view back on for this service; and the exposure is the opposite way round: refusing costs one
    /// staff user's support session, while failing open there would cost every user's every request.
    /// The marker is read from the shared store, never a replica's local copy, so an End handled by
    /// the store host is seen here on the next request.
    /// </remarks>
    private static async Task<bool> IsGrantLiveWithoutTheStoreAsync(HttpContext context, ClaimsPrincipal user, ILogger logger)
    {
        if (!TryReadTokenWithoutTheStore(user, out var grantId, out var expiresAt))
        {
            LogMalformedToken(logger);
            return false;
        }

        var now = (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (expiresAt <= now)
        {
            LogExpiredToken(logger, grantId);
            return false;
        }

        var cache = context.RequestServices.GetService<ICacheService>();
        if (cache is null)
        {
            LogNoSharedCache(logger, grantId);
            return false;
        }

        try
        {
            var ended = await cache
                .GetFromSharedStoreAsync<bool?>(ViewAsEndedMarker.KeyFor(grantId), context.RequestAborted)
                .ConfigureAwait(false);
            if (ended is true)
            {
                LogGrantEnded(logger, grantId);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGrantLookupFailed(logger, grantId, ex);
            return false;
        }
    }

    /// <summary>
    /// Reads what a service without the grant store can check: the grant id and the token's expiry,
    /// with the staff and subject claims required to be present and well formed as on the store host.
    /// </summary>
    private static bool TryReadTokenWithoutTheStore(ClaimsPrincipal user, out Guid grantId, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        return Guid.TryParse(user.FindFirst(AuthClaimTypes.ViewAsGrant)?.Value, out grantId)
            && UserIdentifierType.TryParse(user.FindFirst(AuthClaimTypes.ViewAsActor)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && user.GetUserId() is not null
            && ViewAsEndedMarker.TryGetTokenExpiry(user, out expiresAt);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "View-as request refused: the token of grant {GrantId} has expired")]
    private static partial void LogExpiredToken(ILogger logger, Guid grantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "View-as grant {GrantId} could not be checked: this host holds no grant store and registers no ICacheService; the request is refused (fail closed)")]
    private static partial void LogNoSharedCache(ILogger logger, Guid grantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "View-as request refused: grant {GrantId} was ended")]
    private static partial void LogGrantEnded(ILogger logger, Guid grantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "View-as request refused: {Method} is not allowed under a read-only view-as token")]
    private static partial void LogWriteRefused(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "View-as request refused: view-as is disabled on this host")]
    private static partial void LogViewAsDisabled(ILogger logger);

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
