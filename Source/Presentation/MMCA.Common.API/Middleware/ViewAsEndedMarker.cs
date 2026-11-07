using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// The shared-cache "view-as grant ended" marker (ADR-137) for a multi-service host. Only the service
/// that holds the grant store (<c>ViewAs:HostsGrantStore</c>) can read a grant row; every other service
/// checks this marker in the distributed cache they all share, the way
/// <see cref="Application.Auth.SoftDeletedUserCache"/> carries a delete to services without Identity.
/// </summary>
/// <remarks>
/// <para>
/// The end endpoint is the only path that ends a grant before its expiry, so it is the only writer.
/// A deleted target account is already refused on every service by the soft-deleted-user marker
/// (the token's subject IS the target), and the grant check never re-reads the staff user's
/// permissions, so neither needs a marker of its own.
/// </para>
/// <para>
/// Internal on purpose: the writer and the reader both live in this package.
/// </para>
/// </remarks>
internal static partial class ViewAsEndedMarker
{
    /// <summary>The cache key holding the ended marker for one grant.</summary>
    /// <param name="grantId">The grant (a view-as token's <c>vag</c> claim).</param>
    /// <returns>The cache key.</returns>
    internal static string KeyFor(Guid grantId) => $"viewas:ended:{grantId:D}";

    /// <summary>Reads the token's <c>exp</c> claim (Unix seconds).</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="expiresAt">The token's expiry when the claim is present and well formed.</param>
    /// <returns>Whether a usable expiry was found.</returns>
    internal static bool TryGetTokenExpiry(ClaimsPrincipal user, out DateTimeOffset expiresAt)
    {
        if (long.TryParse(user.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
            && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }

        expiresAt = default;
        return false;
    }

    /// <summary>
    /// Writes the ended marker for a grant just ended on this (the store) host. The marker lives for the
    /// token's remaining life plus the JWT bearer clock skew, because a service accepts the token until
    /// its expiry plus that skew; a marker that lapsed first would let the ended token through again.
    /// </summary>
    /// <param name="services">The request's services; nothing is written when they or the cache are absent.</param>
    /// <param name="user">The view-as principal whose grant ended (its <c>exp</c> claim sets the lifetime).</param>
    /// <param name="grantId">The grant that ended.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the marker is written or the failure is logged.</returns>
    /// <remarks>
    /// The skew is read from this host's JWT bearer options (the library default of 5 minutes when the
    /// host sets none, which is what every framework registration does), on the assumption that the
    /// services of one deployment validate tokens alike. A token with no <c>exp</c> claim falls back to
    /// the longest grant there is, <see cref="ViewAsGrant.MaxLifetime"/>. A failed write is logged, not
    /// thrown: the grant row is already ended, the proxy drops its overlay cookie whatever the answer,
    /// and a service that cannot reach the cache refuses every view-as request anyway.
    /// </remarks>
    internal static async Task WriteAsync(
        IServiceProvider? services,
        ClaimsPrincipal user,
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        if (services?.GetService<ICacheService>() is not { } cache)
        {
            return;
        }

        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var lifetime = RemainingTokenLife(user, now) + ClockSkew(services);

        try
        {
            await cache.SetAsync(KeyFor(grantId), true, lifetime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ViewAsEndedMarker).FullName!)
                ?? NullLogger.Instance;
            LogMarkerWriteFailed(logger, grantId, ex);
        }
    }

    /// <summary>The token's remaining life, or the longest grant there is when it carries no expiry.</summary>
    private static TimeSpan RemainingTokenLife(ClaimsPrincipal user, DateTimeOffset now)
    {
        if (!TryGetTokenExpiry(user, out var expiresAt))
        {
            return ViewAsGrant.MaxLifetime;
        }

        return expiresAt > now ? expiresAt - now : TimeSpan.Zero;
    }

    /// <summary>This host's JWT bearer clock skew, or the library default when none is configured.</summary>
    private static TimeSpan ClockSkew(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<JwtBearerOptions>>()?
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters?
            .ClockSkew
        ?? TokenValidationParameters.DefaultClockSkew;

    [LoggerMessage(Level = LogLevel.Error, Message = "View-as grant {GrantId} ended, but its ended marker could not be written to the shared cache; services without the grant store accept its token until it expires")]
    private static partial void LogMarkerWriteFailed(ILogger logger, Guid grantId, Exception exception);
}
