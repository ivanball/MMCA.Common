using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Sessions;

/// <summary>
/// Issues, rotates and revokes the access/refresh token pair behind a signed-in device: it owns the
/// multi-device <c>RefreshSession</c> rows (hashed at rest), the BR-205 rotation with BR-206 reuse
/// detection, the per-user live-session cap, and the <c>sid</c>/<c>mfa</c> claims stamped on the
/// access token minted for a session.
/// </summary>
/// <remarks>
/// <para>
/// It decides nothing about WHO may sign in: credentials, lockout, second factor and the app's own
/// gates stay with <c>AuthenticationServiceBase</c>, which calls this once a caller has been proved.
/// The access token's claim set also stays with the app; the issuer takes it as a callback
/// (<c>mintAccessToken</c>) because the session id the token must carry only exists once the
/// session has been opened or rotated.
/// </para>
/// <para>
/// Scoped, like the session store it writes through, so a sign-in and its session insert share the
/// request's unit of work.
/// </para>
/// </remarks>
public interface IAuthSessionIssuer
{
    /// <summary>
    /// The token service an app mints access tokens through. While the issuer is minting for a
    /// session (<see cref="MintForSession"/>), this instance appends that session's <c>sid</c> claim,
    /// and the <c>mfa</c> claim when a second factor verified, to whatever claim set the app passes; at
    /// any other time it is the plain registered <see cref="ITokenService"/>.
    /// </summary>
    ITokenService TokenService { get; }

    /// <summary>
    /// Opens a new refresh session for the user (evicting the oldest live one when the per-user cap
    /// is full), persists it, and returns the token pair. The user's other sessions are untouched.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="mintAccessToken">Mints the access token for the new session's id.</param>
    /// <param name="ipAddress">Optional client IP recorded on the session.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token pair, or the failure that prevented the session from opening.</returns>
    Task<Result<AuthenticationResponse>> IssueAsync(
        UserIdentifierType userId,
        Func<Guid, string> mintAccessToken,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rotates the session behind a presented refresh token (BR-205): revokes it, links it to a new
    /// successor session and returns the successor's token pair. An unknown or expired token fails
    /// alone; an already-revoked token, or one a concurrent request rotated first, is the BR-206
    /// reuse signal and revokes every live session the user holds. Every rejection carries the same
    /// <c>Auth.InvalidRefreshToken</c> error.
    /// </summary>
    /// <param name="userId">The user the presented access token names.</param>
    /// <param name="refreshToken">The presented plaintext refresh token.</param>
    /// <param name="mintAccessToken">Mints the access token for the successor session's id.</param>
    /// <param name="ipAddress">Optional client IP recorded on the successor.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the successor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The successor token pair, or the refresh rejection.</returns>
    Task<Result<AuthenticationResponse>> RotateAsync(
        UserIdentifierType userId,
        string refreshToken,
        Func<Guid, string> mintAccessToken,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="mintAccessToken"/> with <see cref="TokenService"/> armed to stamp the
    /// session's <c>sid</c> claim and, when not null, the <c>mfa</c> claim.
    /// </summary>
    /// <param name="sessionId">The refresh session the token is being minted for.</param>
    /// <param name="multiFactorMethod">The second-factor method that verified, or null for none.</param>
    /// <param name="mintAccessToken">Mints the token through <see cref="TokenService"/>.</param>
    /// <returns>The signed access token.</returns>
    string MintForSession(Guid sessionId, string? multiFactorMethod, Func<string> mintAccessToken);

    /// <summary>
    /// Signs one device out: revokes the live session behind <paramref name="refreshToken"/> when it
    /// belongs to the user. When the token is absent or identifies no live session of this user, the
    /// device cannot be identified and every live session is revoked instead.
    /// </summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="refreshToken">The device's refresh token, if the caller presented one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the revocation is saved.</returns>
    Task SignOutAsync(UserIdentifierType userId, string? refreshToken, CancellationToken cancellationToken);

    /// <summary>Revokes every live session the user holds and saves.</summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the revocation is saved.</returns>
    Task SignOutEverywhereAsync(UserIdentifierType userId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the user's live sessions, newest first. Expired-but-unrevoked rows are left out, since a
    /// device list must not offer a device that can no longer authenticate.
    /// </summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="currentSessionId">The caller's own session id, flagged in the result, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live sessions.</returns>
    Task<IReadOnlyList<RefreshSessionSummaryResponse>> ListActiveAsync(
        UserIdentifierType userId,
        Guid? currentSessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes one of the user's sessions by id. Another account's session id and an id that never
    /// existed both answer <c>Auth.SessionNotFound</c>; an already-revoked session is a success that
    /// writes nothing.
    /// </summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="sessionId">The session to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the not-found failure.</returns>
    Task<Result> RevokeSessionAsync(UserIdentifierType userId, Guid sessionId, CancellationToken cancellationToken);
}
