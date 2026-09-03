using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Client-side authentication operations that coordinate token storage, HTTP calls to the
/// <c>auth/*</c> WebAPI endpoints, and Blazor auth-state notifications.
/// <para>
/// Every call that talks to the API returns a <see cref="Result"/> carrying the server's own
/// errors, so the failure travels with the call instead of on a <c>LastError</c> property that the
/// next call would overwrite. Pages render it with
/// <c>MMCA.Common.UI.Common.ResultUiExtensions</c>.
/// </para>
/// </summary>
public interface IAuthUIService
{
    /// <summary>Authenticates the user and stores tokens.</summary>
    Task<Result<AuthenticationResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Registers a new user account, stores tokens, and returns the response.</summary>
    Task<Result<AuthenticationResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a single-use OAuth completion code (carried in the redirect URL) for the
    /// authentication token pair via <c>auth/oauth/exchange</c>, stores the tokens, and notifies
    /// auth state. Keeps tokens out of the address bar.
    /// </summary>
    Task<Result<AuthenticationResponse>> ExchangeOAuthCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out everywhere: revokes the user's server-side refresh sessions and clears local token
    /// storage. Deliberately returns no <see cref="Result"/>: the local sign-out happens whatever
    /// the server answered, because a user who asked to leave must never be kept signed in by a
    /// failed network call.
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Attempts to exchange the current refresh token for a new access token. Stays a
    /// <see cref="bool"/>: it makes no API call of its own (the host's
    /// <see cref="ITokenRefresher"/> owns the exchange) and its two states are "session still
    /// live" and "session gone", neither of which is an error to render.
    /// </summary>
    Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the authenticated user's password via the <c>auth/password</c> endpoint.</summary>
    Task<Result> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a password-reset email via the anonymous <c>auth/forgot-password</c> endpoint. The
    /// endpoint answers 202 for every well-formed address (anti-enumeration), so a success means
    /// "accepted", never "an account exists".
    /// </summary>
    Task<Result> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a password reset via the anonymous <c>auth/reset-password</c> endpoint. An invalid,
    /// expired, or already-consumed token comes back as a failure carrying the server's generic
    /// message.
    /// </summary>
    Task<Result> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the caller's signed-in devices (live refresh sessions, newest first) via
    /// <c>auth/my-sessions</c>. Exactly one row can carry
    /// <see cref="RefreshSessionSummaryResponse.IsCurrent"/>, resolved server-side from the access
    /// token's <c>sid</c> claim.
    /// </summary>
    Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs one device out via <c>auth/revoke/{sessionId}</c>. Another account's session id (or a
    /// nonexistent one) answers 404, which arrives as an <see cref="ErrorType.NotFound"/> failure;
    /// revoking an already-revoked session succeeds.
    /// </summary>
    /// <param name="sessionId">The session to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
