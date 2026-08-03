using System.Globalization;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Shared cache contract for the soft-deleted user marker (BR-133). The API middleware
/// reads this marker on every authenticated request; the module that soft-deletes a user
/// writes it so the deletion takes effect before the next database lookup would notice.
/// </summary>
/// <remarks>
/// Both the key shape and the marker lifetime live here rather than inside the middleware,
/// because a downstream application that deletes an account has to write the exact same key
/// the middleware reads, and a private constant in the presentation layer is unreachable
/// from an application-layer command handler.
/// </remarks>
public static class SoftDeletedUserCache
{
    /// <summary>
    /// Lifetime of the deleted-user marker.
    /// </summary>
    /// <remarks>
    /// The marker only has to outlive the window between the delete committing and the next
    /// token validation: once the marker expires, the validator query is the source of truth
    /// again and reports the same answer. Short-lived access tokens (15 minutes) bound the
    /// rest of the exposure, so a longer marker would buy nothing and would only keep stale
    /// entries alive for users who were never deleted.
    /// </remarks>
    public static TimeSpan MarkerDuration => TimeSpan.FromSeconds(30);

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
    /// Writes the soft-deleted marker for a user, so authenticated requests bearing an
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
}
