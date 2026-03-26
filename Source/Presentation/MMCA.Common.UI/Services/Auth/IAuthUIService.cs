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

    /// <summary>Revokes the server-side refresh token and clears local token storage.</summary>
    Task LogoutAsync();

    /// <summary>Attempts to exchange the current refresh token for a new access token.</summary>
    Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the authenticated user's password via the <c>auth/password</c> endpoint.</summary>
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}
