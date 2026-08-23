using MMCA.Common.Shared.Auth;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Client-side authentication operations that coordinate token storage, HTTP calls to the
/// <c>auth/*</c> WebAPI endpoints, and Blazor auth-state notifications.
/// </summary>
public interface IAuthUIService
{
    /// <summary>Gets the last error message from a failed authentication operation, or <see langword="null"/>.</summary>
    string? LastError { get; }

    /// <summary>Authenticates the user and stores tokens. Returns <see langword="null"/> on failure.</summary>
    Task<AuthenticationResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Registers a new user account, stores tokens, and returns the response.</summary>
    Task<AuthenticationResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a single-use OAuth completion code (carried in the redirect URL) for the
    /// authentication token pair via <c>auth/oauth/exchange</c>, stores the tokens, and notifies
    /// auth state. Returns <see langword="null"/> on failure. Keeps tokens out of the address bar.
    /// </summary>
    Task<AuthenticationResponse?> ExchangeOAuthCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Revokes the server-side refresh token and clears local token storage.</summary>
    Task LogoutAsync();

    /// <summary>Attempts to exchange the current refresh token for a new access token.</summary>
    Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the authenticated user's password via the <c>auth/password</c> endpoint.</summary>
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a password-reset email via the anonymous <c>auth/forgot-password</c> endpoint. The
    /// endpoint answers 202 for every well-formed address (anti-enumeration), so a <see langword="true"/>
    /// result means "accepted", never "an account exists".
    /// </summary>
    Task<bool> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a password reset via the anonymous <c>auth/reset-password</c> endpoint. Returns
    /// <see langword="false"/> for an invalid, expired, or already-consumed token, with the server's
    /// generic message in <see cref="LastError"/>.
    /// </summary>
    Task<bool> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default);
}
