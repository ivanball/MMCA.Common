using System.Globalization;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Shared cache contract for the soft-deleted user marker (BR-133), which means "deleted or
/// locked". The API middleware reads this marker on every authenticated request and answers 401
/// while it is set. The module that soft-deletes a user, an administrator's lock
/// (<c>UserAdministrationServiceBase</c>) and a machine-client revocation write it, so the change
/// takes effect before the next database lookup would notice; an unlock clears it.
/// </summary>
/// <remarks>
/// Both the key shape and the marker lifetime live here rather than inside the middleware,
/// because a downstream application that deletes an account has to write the exact same key
/// the middleware reads, and a private constant in the presentation layer is unreachable
/// from an application-layer command handler.
/// </remarks>
public static class SoftDeletedUserCache
{
    /// <summary>The JWT bearer library's default clock skew, which the framework never overrides.</summary>
    private static readonly TimeSpan DefaultClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Lifetime of the deleted-or-locked marker.
    /// </summary>
    /// <remarks>
    /// The marker must outlive every access token issued before the delete or lock. A host that does
    /// not run Identity registers no validator query, and the Identity host's query checks deletion
    /// only, so the marker is the only thing that rejects a deleted user's still-valid token on the
    /// first and a locked user's on any host; if it expired first, that token would be served
    /// again until it lapsed. A token is accepted until its expiry plus the JWT bearer clock skew,
    /// and the framework sets no <c>ClockSkew</c>, so the library default of 5 minutes applies:
    /// the marker therefore lasts the default access-token lifetime (15 minutes) plus that skew,
    /// 20 minutes. This is a constant rather than a read of the JWT settings because the marker
    /// is written through this static helper, where those settings are not reachable; a host that
    /// raises the access-token lifetime or the clock skew writes a longer marker through
    /// <see cref="MarkerDurationFor"/>. Only positive markers are written: the middleware never caches
    /// a "not deleted" answer.
    /// </remarks>
    public static TimeSpan MarkerDuration => TimeSpan.FromMinutes(20);

    /// <summary>
    /// The lifetime a marker needs on a host with this access-token lifetime: long enough to outlast
    /// an already-issued token, which is accepted until its expiry plus the JWT bearer clock skew, and
    /// never shorter than <see cref="MarkerDuration"/>.
    /// </summary>
    /// <param name="accessTokenLifetime">The host's access-token lifetime.</param>
    /// <param name="clockSkew">The host's JWT bearer clock skew; the library default of 5 minutes when null.</param>
    /// <returns>The marker lifetime to write.</returns>
    public static TimeSpan MarkerDurationFor(TimeSpan accessTokenLifetime, TimeSpan? clockSkew = null)
    {
        var covering = accessTokenLifetime + (clockSkew ?? DefaultClockSkew);
        return covering > MarkerDuration ? covering : MarkerDuration;
    }

    /// <summary>
    /// Builds the cache key holding the soft-deleted marker for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>The cache key for that user's soft-deleted marker.</returns>
    /// <remarks>
    /// Formatted with <see cref="CultureInfo.InvariantCulture"/> on purpose: the identifier
    /// renders differently under some cultures (digit shapes and group separators), so a
    /// culture-sensitive key would be written under one request's culture and missed under
    /// another, silently letting a deleted user keep making requests.
    /// </remarks>
    public static string KeyFor(UserIdentifierType userId) =>
        string.Create(CultureInfo.InvariantCulture, $"user:deleted:{userId}");

    /// <summary>
    /// Writes the deleted-or-locked marker for a user, so authenticated requests bearing an
    /// already-issued token are rejected without waiting for a database lookup.
    /// </summary>
    /// <param name="cache">The cache service to write to.</param>
    /// <param name="userId">The user identifier that was soft-deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task MarkDeletedAsync(
        ICacheService cache,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);

        return cache.SetAsync(KeyFor(userId), true, MarkerDuration, cancellationToken);
    }

    /// <summary>
    /// Writes the deleted-or-locked marker for a user with an explicit lifetime, for a host whose access
    /// tokens outlive <see cref="MarkerDuration"/> (see <see cref="MarkerDurationFor"/>).
    /// </summary>
    /// <param name="cache">The cache service to write to.</param>
    /// <param name="userId">The user identifier that was soft-deleted or locked.</param>
    /// <param name="duration">The marker lifetime.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task MarkDeletedForAsync(
        ICacheService cache,
        UserIdentifierType userId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);

        return cache.SetAsync(KeyFor(userId), true, duration, cancellationToken);
    }

    /// <summary>
    /// Removes a user's marker, so a token issued after the account was unlocked is accepted at once
    /// instead of being refused until the marker expires.
    /// </summary>
    /// <param name="cache">The cache service the marker was written to.</param>
    /// <param name="userId">The user identifier whose marker is removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <see cref="ICacheService.RemoveAsync"/> deletes the shared-store entry (and this process's
    /// local copy). Another replica may keep a local copy for up to its local cache duration, but the
    /// middleware never reads one: it reads the marker through
    /// <see cref="ICacheService.GetFromSharedStoreAsync{T}"/>, so the removal is seen by every host at
    /// once.
    /// </remarks>
    public static Task ClearMarkerAsync(
        ICacheService cache,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);

        return cache.RemoveAsync(KeyFor(userId), cancellationToken);
    }
}
