using Microsoft.AspNetCore.Http;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Web.Services;

/// <summary>
/// Blazor Server token storage (cookie-only, no localStorage). During SSR prerender (a live
/// <see cref="HttpContext"/>) the access token is read from the HttpOnly cookie — refreshed in place by
/// <c>UseCookieSessionRefresh</c> on navigations. On the interactive circuit (no <see cref="HttpContext"/>)
/// the access token is held <b>in memory only</b> and hydrated/refreshed on demand from the HttpOnly cookies
/// via the same-origin <c>/auth/session/token</c> endpoint (<see cref="ITokenRefresher"/>); the refresh
/// token is never readable by JS. Hoisted from the app Blazor Web hosts (it carries no app-specific
/// state); its WASM sibling is <see cref="WasmTokenStorageService"/> in MMCA.Common.UI. Register via
/// <c>AddCommonServerTokenStorage()</c>.
/// </summary>
public sealed class ServerTokenStorageService(
    IHttpContextAccessor httpContextAccessor,
    CookieTokenReader cookieTokenReader,
    ISessionCookieSync sessionCookieSync,
    ITokenRefresher tokenRefresher) : ITokenStorageService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private string? _accessToken;
    private Task<string?>? _hydrateInFlight;

    public async Task<string?> GetAccessTokenAsync()
    {
        // SSR prerender: the HttpOnly cookie (possibly just refreshed by the middleware / stashed in
        // HttpContext.Items) is the source of truth; JS interop is unavailable here.
        if (httpContextAccessor.HttpContext is not null)
        {
            return cookieTokenReader.ReadAccessToken();
        }

        // Interactive circuit: in-memory token, re-acquired from the cookie via JS when missing/near-expiry.
        if (JwtTokenInfo.IsFresh(_accessToken, ExpirySkew))
        {
            return _accessToken;
        }

        // Single-flight: concurrent callers (delegating handler, auth-state, SignalR) share one acquisition.
        var inFlight = _hydrateInFlight ??= HydrateAsync();
        try
        {
            return await inFlight.ConfigureAwait(false);
        }
        finally
        {
            _hydrateInFlight = null;
        }
    }

    public Task<string?> GetRefreshTokenAsync()
    {
        // The refresh token is never held client-side; SSR can read the cookie, the circuit cannot (HttpOnly).
        var refreshToken = httpContextAccessor.HttpContext is not null ? cookieTokenReader.ReadRefreshToken() : null;
        return Task.FromResult(refreshToken);
    }

    public async Task SetTokensAsync(string accessToken, string refreshToken)
    {
        _accessToken = accessToken;
        // Seed the HttpOnly cookies at login. The refresh token transits JS only for this same-origin POST
        // and is never persisted in localStorage.
        await sessionCookieSync.SyncAsync(accessToken, refreshToken);
    }

    public async Task ClearTokensAsync()
    {
        _accessToken = null;
        await sessionCookieSync.ClearAsync();
    }

    private async Task<string?> HydrateAsync()
    {
        _accessToken = await tokenRefresher.AcquireAccessTokenAsync().ConfigureAwait(false);
        return _accessToken;
    }
}
