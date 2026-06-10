namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Acquires a fresh JWT access token, abstracting over how the refresh token is held per host:
/// <list type="bullet">
/// <item>Browser (Server + WASM): <see cref="SameOriginProxyTokenRefresher"/> calls the same-origin
/// <c>/auth/session/token</c> endpoint, where the refresh token lives in an HttpOnly cookie and the
/// rotation happens server-side — the refresh token is never exposed to JS.</item>
/// <item>MAUI: <see cref="DirectApiTokenRefresher"/> exchanges the refresh token held in OS SecureStorage
/// directly against the API's <c>auth/refresh</c> endpoint.</item>
/// </list>
/// </summary>
public interface ITokenRefresher
{
    /// <summary>
    /// Returns a freshly-acquired access token, or <see langword="null"/> when no valid session exists
    /// (e.g., the refresh credential is missing, expired, or revoked). Host-specific refresh-token
    /// persistence (HttpOnly cookie or SecureStorage) is handled internally.
    /// </summary>
    Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default);
}
