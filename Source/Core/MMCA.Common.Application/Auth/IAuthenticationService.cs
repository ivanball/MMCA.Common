using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Defines the authentication workflows for the Identity module: login, registration,
/// token refresh, and token revocation. Password change is dispatched directly through its
/// command handler at the controller layer, not brokered by this service.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Authenticates a user with email and password credentials, opening a new refresh session for
    /// the calling device. Sessions already open on the user's other devices are left alone.
    /// </summary>
    /// <param name="request">Login credentials.</param>
    /// <param name="ipAddress">Optional client IP recorded on the new session.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the new session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the authentication tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> LoginAsync(
        LoginRequest request,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new user account and returns authentication tokens.
    /// </summary>
    /// <param name="request">Registration details.</param>
    /// <param name="ipAddress">Optional client IP address, for rate limiting and the new session.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the new session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the authentication tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> RegisterAsync(
        RegisterRequest request,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges an expired access token and valid refresh token for a new token pair, rotating the
    /// presenting device's session and leaving the user's other sessions untouched.
    /// </summary>
    /// <param name="request">The expired access token and current refresh token.</param>
    /// <param name="ipAddress">Optional client IP recorded on the rotated session.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the rotated session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the rotated tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> RefreshTokenAsync(
        RefreshTokenRequest request,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs one device out: revokes the session behind <paramref name="refreshToken"/>. Passing no
    /// token (or one that does not belong to this user) revokes every session the user holds, which is
    /// the safe reading of "log me out" from a caller that cannot produce its refresh token.
    /// </summary>
    /// <param name="userId">The user whose session should be revoked.</param>
    /// <param name="refreshToken">The refresh token identifying the device to sign out, if known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or a not-found error.</returns>
    Task<Result> RevokeTokenAsync(
        UserIdentifierType userId,
        string? refreshToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs every device out: revokes all of the user's live sessions (a password change, an admin
    /// lockout, or a "sign out everywhere" action).
    /// </summary>
    /// <param name="userId">The user whose sessions should be revoked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or a not-found error.</returns>
    Task<Result> RevokeAllSessionsAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the user's live sessions: one row per signed-in device, newest first, with the caller's
    /// own device marked. Revoked and expired sessions are left out, so the list is exactly what a
    /// "sign this device out" control can act on.
    /// </summary>
    /// <param name="userId">The user whose devices to list.</param>
    /// <param name="currentSessionId">
    /// The session the calling access token was minted for (its <c>sid</c> claim), used only to set
    /// <see cref="RefreshSessionSummaryResponse.IsCurrent"/>. Pass <see langword="null"/> when the
    /// caller's token carries no <c>sid</c>; no row is then marked current.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live sessions, newest first.</returns>
    Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        UserIdentifierType userId,
        Guid? currentSessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs one named device out: revokes the user's session with this identifier.
    /// </summary>
    /// <remarks>
    /// A session id that is unknown, or that belongs to another account, returns <c>NotFound</c> and
    /// is indistinguishable from the other, so a caller cannot probe for another user's sessions.
    /// Revoking a session that is <b>already</b> revoked succeeds and changes nothing: the caller
    /// asked for that device to be signed out and it is, and a device list a user is clicking through
    /// is exactly where a duplicate request comes from.
    /// </remarks>
    /// <param name="userId">The user the session must belong to.</param>
    /// <param name="sessionId">The session to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or a not-found error.</returns>
    Task<Result> RevokeSessionByIdAsync(
        UserIdentifierType userId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a user via an external OAuth provider. Finds an existing account by
    /// provider+key or creates a new one from the OAuth claims.
    /// </summary>
    /// <param name="loginProvider">The OAuth provider name (e.g., "Google", "GitHub").</param>
    /// <param name="providerKey">The provider-specific unique identifier.</param>
    /// <param name="email">Email from OAuth claims.</param>
    /// <param name="firstName">First name from OAuth claims.</param>
    /// <param name="lastName">Last name from OAuth claims.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the authentication tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> ExternalLoginAsync(
        string loginProvider,
        string providerKey,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<AuthenticationResponse>(
            Error.Failure("Auth.ExternalLoginNotSupported", "External login is not supported.")));
}
