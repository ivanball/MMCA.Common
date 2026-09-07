using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Shared "credential rotated, evict every live session" step for the password-change and
/// password-reset workflows.
/// <para>
/// SECURITY: changing or resetting a password is the remediation a user reaches for when they think
/// someone else is inside the account. Leaving the refresh-session rows un-revoked means the
/// intruder's rotating chain keeps minting access tokens for its full lifetime, so the remediation
/// does not remediate. Revocation goes through <see cref="IRefreshSessionStore"/> directly rather
/// than <c>IAuthenticationService</c>: the store returns tracked instances, so revoking is a
/// mutation plus a save with no second user lookup and no cycle back through the auth workflow.
/// </para>
/// </summary>
internal static class RefreshSessionRevocation
{
    /// <summary>
    /// Revokes every un-revoked session the user holds. A host that registered no
    /// <see cref="IRefreshSessionStore"/> (refresh sessions are opt-in) is a no-op.
    /// </summary>
    /// <param name="refreshSessions">The session store, or <see langword="null"/> when the host wired none.</param>
    /// <param name="timeProvider">Clock used to stamp the revocation; defaults to the system clock.</param>
    /// <param name="userId">The user whose sessions to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task RevokeAllAsync(
        IRefreshSessionStore? refreshSessions,
        TimeProvider? timeProvider,
        UserIdentifierType userId,
        CancellationToken cancellationToken)
    {
        if (refreshSessions is null)
        {
            return;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var sessions = await refreshSessions.GetUnrevokedByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            return;
        }

        foreach (var session in sessions)
        {
            session.Revoke(now, RefreshSession.ReasonSignedOut);
        }

        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
