namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Synchronizes the browser's HttpOnly auth cookie with the client's in-memory tokens. The cookie is
/// what SSR prerender reads — the interactive circuit's in-memory access token is unreachable from the
/// server. Without this sync, right-click → "Open in new tab" on an <c>[Authorize]</c> page redirects to /login.
/// </summary>
public interface ISessionCookieSync
{
    /// <summary>Writes the session cookies for the given token pair.</summary>
    /// <param name="accessToken">The access token to mirror into the cookie.</param>
    /// <param name="refreshToken">The refresh token to mirror into the cookie.</param>
    /// <returns><see langword="true"/> when the cookie jar was updated; <see langword="false"/> when the write
    /// failed or could not be attempted (non-2xx, dropped connection, JS interop unavailable).</returns>
    Task<bool> SyncAsync(string accessToken, string refreshToken);

    /// <summary>Clears the session cookies.</summary>
    /// <returns><see langword="true"/> when the cookie jar was updated; <see langword="false"/> when the clear
    /// failed or could not be attempted.</returns>
    Task<bool> ClearAsync();
}
