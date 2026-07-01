namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// WebAssembly token storage (cookie-only, no localStorage). The access token lives <b>in memory only</b>
/// and is hydrated/refreshed on demand from the HttpOnly cookies via the same-origin
/// <c>/auth/session/token</c> endpoint (<see cref="ITokenRefresher"/>). The refresh token is never readable
/// by JS. <see cref="SetTokensAsync"/> seeds the cookies at login via <see cref="ISessionCookieSync"/>.
/// Hoisted from the app WASM clients (it carries no app-specific state); its Blazor Server sibling is
/// <c>ServerTokenStorageService</c> in MMCA.Common.UI.Web.
/// </summary>
public sealed class WasmTokenStorageService(
    ISessionCookieSync sessionCookieSync,
    ITokenRefresher tokenRefresher) : ITokenStorageService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private string? _accessToken;
    private Task<string?>? _hydrateInFlight;

    public async Task<string?> GetAccessTokenAsync()
    {
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

    // The refresh token is never held client-side in the browser — it lives only in the HttpOnly cookie.
    public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>(null);

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
