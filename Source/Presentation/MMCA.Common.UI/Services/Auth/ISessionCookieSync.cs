namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Synchronizes the browser's HttpOnly auth cookie with the client's in-memory tokens. The cookie is
/// what SSR prerender reads — the interactive circuit's in-memory access token is unreachable from the
/// server. Without this sync, right-click → "Open in new tab" on an <c>[Authorize]</c> page redirects to /login.
/// </summary>
public interface ISessionCookieSync
{
    Task SyncAsync(string accessToken, string refreshToken);

    Task ClearAsync();
}
