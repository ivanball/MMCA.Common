using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Sessions;

/// <summary>
/// Folds a user's refresh-session rows into a sign-in history: one <see cref="SignInSummaryResponse"/>
/// per <see cref="RefreshSession.SignInId"/>, newest sign-in first, capped at <see cref="MaxEntries"/>.
/// Shared by the self-service list (<see cref="AuthSessionIssuer.ListSignInsAsync"/>) and the
/// administrator's (<c>UserSessionsAdministrationService.GetSignInsAsync</c>), so both read a history
/// the same way.
/// </summary>
/// <remarks>
/// <para>
/// A sign-in starts at its earliest retained row and was last active at its newest row's issue
/// instant (every rotation is a refresh, so the newest row is the last activity). The newest row also
/// decides how it ended: live (no end), revoked (its instant and reason), or expired (its expiry).
/// </para>
/// <para>
/// A row with no <see cref="RefreshSession.SignInId"/> predates the column. It is shown as its own
/// sign-in unless it was rotated away (its successor stands for it), and that fallback ages out with
/// the retention sweep (<see cref="RefreshSessionSettings.RetentionDays"/>).
/// </para>
/// </remarks>
internal static class SignInHistory
{
    /// <summary>The most sign-ins one history returns, newest first.</summary>
    internal const int MaxEntries = 50;

    /// <summary>Builds the history for one user's rows.</summary>
    /// <param name="sessions">The user's session rows, any order; rows of other users must not be passed.</param>
    /// <param name="now">The UTC instant that decides which sign-ins have expired.</param>
    /// <param name="currentSessionId">The caller's own session id (its <c>sid</c>), or null.</param>
    /// <returns>At most <see cref="MaxEntries"/> sign-ins, newest first.</returns>
    internal static IReadOnlyList<SignInSummaryResponse> Summarize(
        IEnumerable<RefreshSession> sessions,
        DateTime now,
        Guid? currentSessionId) =>
    [
        .. sessions
            .Where(s => s.SignInId is not null
                || !string.Equals(s.ReasonRevoked, RefreshSession.ReasonRotated, StringComparison.Ordinal))
            .GroupBy(s => s.SignInId ?? s.Id)
            .Select(chain => Summarize(chain.Key, [.. chain], now, currentSessionId))
            .OrderByDescending(s => s.StartedAt)
            .ThenByDescending(s => s.SignInId)
            .Take(MaxEntries),
    ];

    private static SignInSummaryResponse Summarize(
        Guid signInId,
        List<RefreshSession> chain,
        DateTime now,
        Guid? currentSessionId)
    {
        var head = chain.MaxBy(s => (s.CreatedAt, s.Id))!;
        var (endedAt, endReason) = DescribeEnd(head, now);

        return new SignInSummaryResponse(
            signInId,
            StartedAt: chain.Min(s => s.CreatedAt),
            LastActiveAt: head.CreatedAt,
            head.IpAddress,
            head.UserAgent,
            endedAt,
            endReason,
            IsCurrent: currentSessionId is { } current && chain.Exists(s => s.Id == current));
    }

    private static (DateTime? EndedAt, string? EndReason) DescribeEnd(RefreshSession head, DateTime now)
    {
        if (head.RevokedAt is { } revokedAt)
        {
            // The session reasons and SignInEndReasons share their values (SignedOut, ReuseDetected,
            // SessionCapExceeded, AccountDeleted), so the recorded reason is reported as it is.
            return (revokedAt, head.ReasonRevoked);
        }

        return head.ExpiresAt > now ? (null, null) : (head.ExpiresAt, SignInEndReasons.Expired);
    }
}
