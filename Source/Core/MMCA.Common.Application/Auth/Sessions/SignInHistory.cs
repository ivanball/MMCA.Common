using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Sessions;

/// <summary>
/// Reads a user's sign-in history: the store aggregates the rotation chains
/// (<see cref="IRefreshSessionStore.GetRecentSignInsAsync"/>, bounded by <see cref="MaxEntries"/>) and
/// this shapes each one into a <see cref="SignInSummaryResponse"/>. Shared by the self-service list
/// (<see cref="AuthSessionIssuer.ListSignInsAsync"/>) and the administrator's
/// (<c>UserSessionsAdministrationService.GetSignInsAsync</c>), so both read a history the same way.
/// </summary>
/// <remarks>
/// A sign-in starts at its earliest retained row and was last active at its newest row's issue
/// instant (every rotation is a refresh, so the newest row is the last activity). The newest row also
/// decides how it ended: live (no end), revoked (its instant and reason), or expired (its expiry).
/// Nothing here reads more than the store's capped aggregate, however many times a sign-in rotated.
/// </remarks>
internal static class SignInHistory
{
    /// <summary>The most sign-ins one history returns, newest first.</summary>
    internal const int MaxEntries = 50;

    /// <summary>Reads and shapes one user's history.</summary>
    /// <param name="store">The session store that aggregates the chains.</param>
    /// <param name="userId">The user whose sign-ins to read.</param>
    /// <param name="now">The UTC instant that decides which sign-ins have expired.</param>
    /// <param name="currentSessionId">The caller's own session id (its <c>sid</c>), or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>At most <see cref="MaxEntries"/> sign-ins, newest first.</returns>
    internal static async Task<IReadOnlyList<SignInSummaryResponse>> ReadAsync(
        IRefreshSessionStore store,
        UserIdentifierType userId,
        DateTime now,
        Guid? currentSessionId,
        CancellationToken cancellationToken)
    {
        var signIns = await store.GetRecentSignInsAsync(userId, MaxEntries, cancellationToken).ConfigureAwait(false);
        var currentSignInId = await ResolveCurrentSignInAsync(store, userId, signIns, currentSessionId, cancellationToken)
            .ConfigureAwait(false);

        return Summarize(signIns, now, currentSignInId);
    }

    /// <summary>Shapes store aggregates into responses: pure, no I/O.</summary>
    /// <param name="signIns">The aggregated sign-ins of one user.</param>
    /// <param name="now">The UTC instant that decides which sign-ins have expired.</param>
    /// <param name="currentSignInId">The sign-in the caller's own session belongs to, or null.</param>
    /// <returns>At most <see cref="MaxEntries"/> sign-ins, newest first.</returns>
    internal static IReadOnlyList<SignInSummaryResponse> Summarize(
        IEnumerable<RefreshSessionSignIn> signIns,
        DateTime now,
        Guid? currentSignInId) =>
    [
        .. signIns
            .Select(signIn => Summarize(signIn, now, currentSignInId))
            .OrderByDescending(s => s.StartedAt)
            .ThenByDescending(s => s.SignInId)
            .Take(MaxEntries),
    ];

    /// <summary>
    /// The sign-in the caller's own session belongs to. The caller's <c>sid</c> is normally its sign-in's
    /// head (each rotation mints the token for the successor), so no read is needed; otherwise (an
    /// access token minted before a sibling rotated) one user-scoped primary-key lookup finds it.
    /// </summary>
    private static async Task<Guid?> ResolveCurrentSignInAsync(
        IRefreshSessionStore store,
        UserIdentifierType userId,
        IReadOnlyList<RefreshSessionSignIn> signIns,
        Guid? currentSessionId,
        CancellationToken cancellationToken)
    {
        if (currentSessionId is not { } sessionId)
        {
            return null;
        }

        var asHead = signIns.FirstOrDefault(s => s.Head.Id == sessionId);
        if (asHead is not null)
        {
            return asHead.SignInId;
        }

        var current = await store.FindByIdAsync(sessionId, userId, cancellationToken).ConfigureAwait(false);
        return current is null ? null : current.SignInId ?? current.Id;
    }

    private static SignInSummaryResponse Summarize(RefreshSessionSignIn signIn, DateTime now, Guid? currentSignInId)
    {
        var head = signIn.Head;
        var (endedAt, endReason) = DescribeEnd(head, now);

        return new SignInSummaryResponse(
            signIn.SignInId,
            signIn.StartedAt,
            LastActiveAt: head.CreatedAt,
            head.IpAddress,
            head.UserAgent,
            endedAt,
            endReason,
            IsCurrent: currentSignInId is { } current && signIn.SignInId == current);
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
