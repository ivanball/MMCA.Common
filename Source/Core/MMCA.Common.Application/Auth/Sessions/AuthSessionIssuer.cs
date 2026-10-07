using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Sessions;

/// <summary>
/// The framework's <see cref="IAuthSessionIssuer"/>: multi-device refresh sessions, hashed at rest.
/// </summary>
/// <remarks>
/// <para>
/// Every issue opens its own <see cref="RefreshSession"/>, so signing in on a second device leaves the
/// first device signed in, and the store holds only <see cref="RefreshSession.HashToken"/> digests.
/// Rotation revokes the presented session and links it to its successor; presenting an
/// already-rotated token lands on that revoked row, which is the reuse signal that revokes the user's
/// whole live family (BR-206). Two requests presenting the same live token at the same instant are
/// covered by the same rule: the rotation is claimed atomically through
/// <see cref="IRefreshSessionStore.TryRotateAsync"/>, and the request that loses the claim is never
/// handed a second successor. Both paths share one carve-out: a token rotated less than
/// <see cref="RefreshSessionSettings.ReuseGraceSeconds"/> ago (default 10) is a rotation race between
/// sibling requests, answered <c>409 Conflict</c> (<c>Auth.RefreshSuperseded</c>) with nothing revoked;
/// past the grace it is a replay. An expired session is not a reuse signal and fails alone. A per-user cap (<see cref="RefreshSessionSettings.MaxActiveSessionsPerUser"/>)
/// evicts the oldest live session on a new sign-in so one account cannot grow the table without bound.
/// </para>
/// <para>
/// Lifetimes come from the token service (<c>Jwt:AccessTokenExpirationMinutes</c>,
/// <c>Jwt:RefreshTokenExpirationDays</c>) so the expiry reported to clients matches the JWT's actual
/// <c>exp</c>; a non-positive value (a test double or a misconfigured host) falls back to the BR-205
/// defaults of 15 minutes and 7 days.
/// </para>
/// </remarks>
/// <param name="tokenService">Mints access and refresh tokens.</param>
/// <param name="refreshSessions">The multi-device refresh-session store.</param>
/// <param name="refreshSessionSettings">Refresh-session options, including the per-user cap.</param>
/// <param name="timeProvider">The clock every session instant is stamped from.</param>
public sealed class AuthSessionIssuer(
    ITokenService tokenService,
    IRefreshSessionStore refreshSessions,
    IOptions<RefreshSessionSettings> refreshSessionSettings,
    TimeProvider timeProvider) : IAuthSessionIssuer
{
    /// <summary>
    /// The token service handed to callers, wrapped so that a token minted while a session is being
    /// opened or rotated carries that session's <c>sid</c> claim without the app's claim code knowing
    /// it exists.
    /// </summary>
    private readonly SessionStampingTokenService _sessionStampingTokenService = new(tokenService);

    /// <inheritdoc />
    public ITokenService TokenService => _sessionStampingTokenService;

    private TimeSpan AccessTokenLifetime =>
        tokenService.AccessTokenLifetime > TimeSpan.Zero ? tokenService.AccessTokenLifetime : TimeSpan.FromMinutes(15);

    private TimeSpan RefreshTokenLifetime =>
        tokenService.RefreshTokenLifetime > TimeSpan.Zero ? tokenService.RefreshTokenLifetime : TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> IssueAsync(
        UserIdentifierType userId,
        Func<Guid, string> mintAccessToken,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mintAccessToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The session is opened BEFORE the access token is minted, because the token carries the
        // session's id in its `sid` claim and a session only has an id once it has been created.
        var opened = await OpenSessionAsync(userId, now, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(opened.Errors);
        }

        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new AuthenticationResponse(
            mintAccessToken(opened.Value!.SessionId),
            opened.Value.RefreshToken,
            now.Add(AccessTokenLifetime)));
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> RotateAsync(
        UserIdentifierType userId,
        string refreshToken,
        Func<Guid, string> mintAccessToken,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mintAccessToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessionResult = await ResolveRotatableSessionAsync(userId, refreshToken, now, cancellationToken)
            .ConfigureAwait(false);

        if (sessionResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(sessionResult.Errors);
        }

        var rotated = await RotateSessionAsync(sessionResult.Value!, userId, now, ipAddress, userAgent, cancellationToken)
            .ConfigureAwait(false);
        if (rotated.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(rotated.Errors);
        }

        // The successor session is a NEW row with a new id, so the access token handed back carries a
        // new `sid` too: a client's current-device marker follows the rotation instead of pointing at
        // the session the rotation just revoked.
        return Result.Success(new AuthenticationResponse(
            mintAccessToken(rotated.Value!.SessionId),
            rotated.Value.RefreshToken,
            now.Add(AccessTokenLifetime)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The arming is a plain field because this issuer is resolved per request (scoped, like the store
    /// it saves through) and one request mints one token at a time.
    /// </remarks>
    public string MintForSession(Guid sessionId, string? multiFactorMethod, Func<string> mintAccessToken)
    {
        ArgumentNullException.ThrowIfNull(mintAccessToken);

        _sessionStampingTokenService.CurrentSessionId = sessionId;
        _sessionStampingTokenService.CurrentMultiFactorMethod = multiFactorMethod;
        try
        {
            return mintAccessToken();
        }
        finally
        {
            _sessionStampingTokenService.CurrentSessionId = null;
            _sessionStampingTokenService.CurrentMultiFactorMethod = null;
        }
    }

    /// <inheritdoc />
    public async Task SignOutAsync(UserIdentifierType userId, string? refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var session = await refreshSessions
                .FindByTokenHashAsync(RefreshSession.HashToken(refreshToken), cancellationToken)
                .ConfigureAwait(false);

            // Only a live session of this user's identifies the device to sign out. Anything else
            // (unknown token, another account's token, an already-revoked row) leaves the caller
            // unidentifiable, so the request degrades to signing every device out rather than
            // reporting success for a revocation that reached nothing.
            if (session is not null
                && EqualityComparer<UserIdentifierType>.Default.Equals(session.UserId, userId)
                && !session.IsRevoked)
            {
                session.Revoke(now, RefreshSession.ReasonSignedOut);
                await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await RevokeLiveSessionsAsync(userId, RefreshSession.ReasonSignedOut, now, cancellationToken).ConfigureAwait(false);
        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SignOutEverywhereAsync(UserIdentifierType userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await RevokeLiveSessionsAsync(userId, RefreshSession.ReasonSignedOut, now, cancellationToken).ConfigureAwait(false);
        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads through the same "un-revoked sessions for this user" query the cap and family revocation
    /// use, then drops the expired ones in memory: the store returns expired-but-unrevoked rows on
    /// purpose (they still occupy a row against the table). No user lookup is involved, so a list is
    /// one query.
    /// </remarks>
    public async Task<IReadOnlyList<RefreshSessionSummaryResponse>> ListActiveAsync(
        UserIdentifierType userId,
        Guid? currentSessionId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessions = await refreshSessions.GetUnrevokedByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return
        [
            .. sessions
                .Where(s => s.IsActiveAt(now))
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .Select(s => new RefreshSessionSummaryResponse(
                    s.Id,
                    s.CreatedAt,
                    s.ExpiresAt,
                    s.IpAddress,
                    s.UserAgent,
                    currentSessionId is { } current && s.Id == current))
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    /// The ownership check is the store query itself (<see cref="IRefreshSessionStore.FindByIdAsync"/>
    /// is scoped to the user), so another account's session id and an id that never existed produce
    /// the same <c>NotFound</c> and neither confirms the other user's session exists.
    /// <para>
    /// An already-revoked session writes nothing and answers <c>NotFound</c>
    /// (<c>Auth.SessionAlreadyRevoked</c>): the device list a user clicks through rendered it as live,
    /// so the client needs to know it was signed out earlier (a duplicate click, or a session the cap
    /// evicted between render and click) to say so instead of claiming this click signed it out. The
    /// session is the caller's own (the lookup above is user-scoped), so the answer reveals nothing
    /// about another account.
    /// </para>
    /// </remarks>
    public async Task<Result> RevokeSessionAsync(
        UserIdentifierType userId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await refreshSessions.FindByIdAsync(sessionId, userId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Result.Failure(Error.NotFoundError(
                "Auth.SessionNotFound",
                "The session was not found.",
                nameof(IAuthenticationService.RevokeSessionByIdAsync),
                nameof(RefreshSession)));
        }

        if (session.IsRevoked)
        {
            return Result.Failure(Error.NotFoundError(
                "Auth.SessionAlreadyRevoked",
                "The session was already signed out.",
                nameof(IAuthenticationService.RevokeSessionByIdAsync),
                nameof(RefreshSession)));
        }

        session.Revoke(timeProvider.GetUtcNow().UtcDateTime, RefreshSession.ReasonSignedOut);
        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// The refresh rejection, shared by every failing branch so a caller cannot tell an unknown token
    /// from an expired one from a replayed one (the reuse case still revokes the family internally).
    /// </summary>
    private static Error InvalidRefreshTokenError() =>
        Error.Unauthorized(
            "Auth.InvalidRefreshToken",
            "Invalid or expired refresh token.",
            nameof(IAuthenticationService.RefreshTokenAsync));

    /// <summary>
    /// The answer to a token that a sibling request rotated within
    /// <see cref="RefreshSessionSettings.ReuseGraceSeconds"/>: a transient <c>409 Conflict</c>, so the
    /// client keeps its session and retries with the winner's token instead of being signed out.
    /// </summary>
    private static Error RefreshSupersededError() =>
        Error.Conflict(
            "Auth.RefreshSuperseded",
            "The refresh token was already rotated by a concurrent request; retry with the current session.",
            nameof(IAuthenticationService.RefreshTokenAsync));

    /// <summary>
    /// Resolves the session behind a presented refresh token and decides whether it may be rotated.
    /// The three rejections are deliberately different in what they do behind an identical error:
    /// an unknown hash (or one belonging to another account) says nothing about a live session and is
    /// failed alone, since revoking the family on it would let anyone holding one of this user's
    /// expired access tokens sign them out everywhere by posting a random token; a <b>revoked</b> row
    /// splits by why it was revoked: one already rotated away (or already flagged as reuse) has come
    /// back, which is the BR-206 reuse signal that revokes every live session the user holds (unless it
    /// was rotated within <see cref="RefreshSessionSettings.ReuseGraceSeconds"/>, a race answered 409
    /// with nothing revoked), while one
    /// that was signed out or evicted by the session cap only lost its session, so that request fails
    /// alone; an <b>expired</b> row is an ordinary end of life, so that device re-authenticates and
    /// the user's other devices keep working.
    /// </summary>
    private async Task<Result<RefreshSession>> ResolveRotatableSessionAsync(
        UserIdentifierType userId,
        string refreshToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Failure<RefreshSession>(InvalidRefreshTokenError());
        }

        var session = await refreshSessions
            .FindByTokenHashAsync(RefreshSession.HashToken(refreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (session is null || !EqualityComparer<UserIdentifierType>.Default.Equals(session.UserId, userId))
        {
            return Result.Failure<RefreshSession>(InvalidRefreshTokenError());
        }

        if (session.IsRevoked)
        {
            if (!IsReuseSignal(session))
            {
                // Signed out (one device, everywhere, password change) or evicted by the session cap:
                // this device simply lost its session, which is not a theft signal, so only this
                // request fails and the user's other live sessions keep working.
                return Result.Failure<RefreshSession>(InvalidRefreshTokenError());
            }

            if (IsWithinRotationGrace(session, now))
            {
                // A sibling request (another tab, or this browser served by another replica) rotated
                // this token a moment ago: a race, not a replay, so nothing is revoked.
                return Result.Failure<RefreshSession>(RefreshSupersededError());
            }

            await RevokeLiveSessionsAsync(userId, RefreshSession.ReasonReuseDetected, now, cancellationToken)
                .ConfigureAwait(false);
            await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Failure<RefreshSession>(InvalidRefreshTokenError());
        }

        return session.ExpiresAt <= now
            ? Result.Failure<RefreshSession>(InvalidRefreshTokenError())
            : Result.Success(session);
    }

    /// <summary>
    /// Mints a refresh token, opens its session, and stages the insert (without saving). Evicts the
    /// user's oldest live session first when the cap is already full.
    /// </summary>
    /// <returns>The plaintext refresh token to hand to the client, and the new session's id.</returns>
    private async Task<Result<IssuedSession>> OpenSessionAsync(
        UserIdentifierType userId,
        DateTime now,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var refreshToken = tokenService.GenerateRefreshToken();
        var sessionResult = RefreshSession.Create(
            userId,
            refreshToken,
            now,
            now.Add(RefreshTokenLifetime),
            ipAddress,
            userAgent);

        if (sessionResult.IsFailure)
        {
            return Result.Failure<IssuedSession>(sessionResult.Errors);
        }

        var session = sessionResult.Value!;
        await EnforceSessionCapAsync(userId, now, cancellationToken).ConfigureAwait(false);
        await refreshSessions.AddAsync(session, cancellationToken).ConfigureAwait(false);

        return Result.Success(new IssuedSession(refreshToken, session.Id));
    }

    /// <summary>
    /// Revokes the presented session, links it to a freshly minted successor, and persists both
    /// (BR-205 rotation). Rotation replaces one session with one, so the cap is not re-evaluated here.
    /// <para>
    /// The revocation is a <see cref="IRefreshSessionStore.TryRotateAsync"/> claim rather than an
    /// in-memory mutation: two requests presenting the same still-live token both read an un-revoked
    /// row, and the store is what decides which of them owns the rotation. The loser re-reads the row
    /// untracked (<see cref="IRefreshSessionStore.FindByIdUntrackedAsync"/>): a rotation within
    /// <see cref="RefreshSessionSettings.ReuseGraceSeconds"/> is answered 409 with nothing revoked, and
    /// anything else is answered exactly like a replay (family revoked, BR-206).
    /// </para>
    /// </summary>
    /// <returns>The plaintext successor token to hand to the client, and the successor's session id.</returns>
    private async Task<Result<IssuedSession>> RotateSessionAsync(
        RefreshSession session,
        UserIdentifierType userId,
        DateTime now,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var refreshToken = tokenService.GenerateRefreshToken();
        var successorResult = RefreshSession.Create(
            userId,
            refreshToken,
            now,
            now.Add(RefreshTokenLifetime),
            ipAddress,
            userAgent);

        if (successorResult.IsFailure)
        {
            return Result.Failure<IssuedSession>(successorResult.Errors);
        }

        var successor = successorResult.Value!;
        var rotated = await refreshSessions
            .TryRotateAsync(session, successor, now, cancellationToken)
            .ConfigureAwait(false);

        if (!rotated)
        {
            // Another request claimed this exact token first, so this one is holding a token that has
            // already been spent. The tracked copy was read before that claim and still shows the row
            // live, so the row is re-read as the database holds it now: a rotation inside the grace
            // is a race and is answered 409 with nothing revoked.
            var current = await refreshSessions
                .FindByIdUntrackedAsync(session.Id, cancellationToken)
                .ConfigureAwait(false);
            if (current is not null && IsWithinRotationGrace(current, now))
            {
                return Result.Failure<IssuedSession>(RefreshSupersededError());
            }

            // Rotated longer ago than the grace, revoked for another reason, or unreadable: that is
            // indistinguishable from a replay, and it gets the replay answer: the whole live family
            // goes (BR-206).
            await RevokeLiveSessionsAsync(userId, RefreshSession.ReasonReuseDetected, now, cancellationToken)
                .ConfigureAwait(false);
            await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Failure<IssuedSession>(InvalidRefreshTokenError());
        }

        return Result.Success(new IssuedSession(refreshToken, successor.Id));
    }

    /// <summary>
    /// Whether presenting this revoked session's token is token reuse (BR-206): it was already
    /// rotated (the rotated reason or a successor hash) or already flagged as reuse. A session that
    /// was signed out or evicted by the cap is not; any other or missing reason is treated as reuse,
    /// which keeps the conservative answer for rows this code does not recognize.
    /// </summary>
    private static bool IsReuseSignal(RefreshSession session)
    {
        if (session.ReplacedByTokenHash is not null)
        {
            return true;
        }

        return session.ReasonRevoked is not (RefreshSession.ReasonSignedOut or RefreshSession.ReasonSessionCap);
    }

    /// <summary>
    /// Whether a revoked session was rotated less than
    /// <see cref="RefreshSessionSettings.ReuseGraceSeconds"/> ago, which makes a second presentation of
    /// its token a rotation race rather than token reuse. Only the <c>Rotated</c> reason qualifies: a
    /// row already flagged as reuse, signed out or evicted never does, however recent. A revocation
    /// stamped slightly ahead of this clock (another replica's) counts as inside the grace.
    /// </summary>
    private bool IsWithinRotationGrace(RefreshSession session, DateTime now)
    {
        var grace = TimeSpan.FromSeconds(refreshSessionSettings.Value.ReuseGraceSeconds);

        return grace > TimeSpan.Zero
            && string.Equals(session.ReasonRevoked, RefreshSession.ReasonRotated, StringComparison.Ordinal)
            && session.RevokedAt is { } revokedAt
            && now - revokedAt < grace;
    }

    /// <summary>Revokes every un-revoked session the user holds, without saving.</summary>
    private async Task RevokeLiveSessionsAsync(
        UserIdentifierType userId,
        string reason,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var sessions = await refreshSessions.GetUnrevokedByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            session.Revoke(now, reason);
        }
    }

    /// <summary>
    /// Makes room for one more session: while the user is at or over the cap
    /// (<c>RefreshSessions:MaxActiveSessionsPerUser</c>, default 10, validated to the range 1-1000 at
    /// startup), revokes the oldest live one rather than refusing the sign-in. Expired-but-unrevoked
    /// rows do not count against the cap (they authenticate nobody); they age out with the
    /// framework's own retention sweep over the table (<c>RefreshSessionCleanupService</c>, window
    /// <c>RefreshSessions:RetentionDays</c>), which the host runs automatically once
    /// <c>RefreshSessions:Enabled</c> is set.
    /// </summary>
    private async Task EnforceSessionCapAsync(UserIdentifierType userId, DateTime now, CancellationToken cancellationToken)
    {
        var cap = refreshSessionSettings.Value.MaxActiveSessionsPerUser;
        var live = (await refreshSessions.GetUnrevokedByUserAsync(userId, cancellationToken).ConfigureAwait(false))
            .Where(s => s.IsActiveAt(now))
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToList();

        for (var index = 0; index <= live.Count - cap; index++)
        {
            live[index].Revoke(now, RefreshSession.ReasonSessionCap);
        }
    }

    /// <summary>
    /// What opening or rotating a session produces: the plaintext token the client is handed (which
    /// exists nowhere else, since the store keeps only its hash) and the row's id, which is what the
    /// access token's <c>sid</c> claim carries.
    /// </summary>
    private sealed record IssuedSession(string RefreshToken, Guid SessionId);

    /// <summary>
    /// Pass-through <see cref="ITokenService"/> that appends the current session's <c>sid</c> claim
    /// to every access token minted while <see cref="CurrentSessionId"/> is armed, and is the plain
    /// inner service the rest of the time.
    /// </summary>
    /// <remarks>
    /// This is what keeps the claim additive. The alternative (an extra parameter on the app's
    /// <c>CreateAccessToken</c> hook) would be a compile break for every consumer, for a claim the
    /// app has no decision to make about.
    /// </remarks>
    private sealed class SessionStampingTokenService(ITokenService inner) : ITokenService
    {
        /// <summary>The session whose id is stamped on the next token, or null to mint unchanged.</summary>
        public Guid? CurrentSessionId { get; set; }

        /// <summary>
        /// The <c>mfa</c> claim value stamped on the next token, or null to mint no such claim. Set
        /// only when a second factor really verified for this request.
        /// </summary>
        public string? CurrentMultiFactorMethod { get; set; }

        /// <inheritdoc />
        public TimeSpan AccessTokenLifetime => inner.AccessTokenLifetime;

        /// <inheritdoc />
        public TimeSpan RefreshTokenLifetime => inner.RefreshTokenLifetime;

        /// <inheritdoc />
        public string GenerateAccessToken(
            UserIdentifierType userId,
            string email,
            string role,
            string fullName,
            IEnumerable<Claim>? additionalClaims = null)
        {
            if (CurrentSessionId is null && CurrentMultiFactorMethod is null)
            {
                return inner.GenerateAccessToken(userId, email, role, fullName, additionalClaims);
            }

            List<Claim> claims = additionalClaims is null ? [] : [.. additionalClaims];

            if (CurrentSessionId is { } sessionId)
            {
                // "D" (lower-case, hyphenated) is the canonical Guid text form, and the one
                // ClaimsPrincipalExtensions.FindSessionId parses back.
                claims.Add(new Claim(AuthClaimTypes.SessionId, sessionId.ToString("D", CultureInfo.InvariantCulture)));
            }

            if (CurrentMultiFactorMethod is { } method)
            {
                claims.Add(new Claim(AuthClaimTypes.MultiFactor, method));
            }

            return inner.GenerateAccessToken(userId, email, role, fullName, claims);
        }

        /// <inheritdoc />
        public string GenerateRefreshToken() => inner.GenerateRefreshToken();

        /// <inheritdoc />
        public ClaimsPrincipal? GetPrincipalFromExpiredToken(string token) =>
            inner.GetPrincipalFromExpiredToken(token);
    }
}
