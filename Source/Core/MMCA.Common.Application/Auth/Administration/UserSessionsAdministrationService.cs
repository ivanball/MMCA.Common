using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Administration;

/// <summary>
/// Default <see cref="IUserSessionsAdministrationService"/> over <see cref="IRefreshSessionStore"/>
/// and <see cref="TimeProvider"/>.
/// </summary>
/// <param name="refreshSessions">The refresh session store.</param>
/// <param name="timeProvider">The clock that decides which sessions are still live.</param>
public sealed class UserSessionsAdministrationService(
    IRefreshSessionStore refreshSessions,
    TimeProvider timeProvider) : IUserSessionsAdministrationService
{
    /// <inheritdoc />
    /// <remarks>
    /// The same read the signed-in devices page makes: the store's un-revoked rows for the user, with
    /// the expired ones dropped in memory (the store returns expired-but-unrevoked rows on purpose),
    /// newest first. No user lookup is made, so an unknown user is an empty list rather than a 404:
    /// the endpoint reveals nothing about an account that <c>GET Admin/Users/{userId}</c> does not.
    /// </remarks>
    public async Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessions = await refreshSessions.GetUnrevokedByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<RefreshSessionSummaryResponse> live =
        [
            .. sessions
                .Where(s => s.UserId == userId && s.IsActiveAt(now))
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .Select(s => new RefreshSessionSummaryResponse(
                    s.Id,
                    s.CreatedAt,
                    s.ExpiresAt,
                    s.IpAddress,
                    s.UserAgent,
                    IsCurrent: false)),
        ];

        return Result.Success(live);
    }
}
