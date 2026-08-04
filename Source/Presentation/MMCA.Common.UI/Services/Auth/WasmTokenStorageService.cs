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

    private readonly Lock _hydrateSync = new();

    private string? _accessToken;
    private Task<string?>? _hydrateInFlight;

    public async Task<string?> GetAccessTokenAsync()
    {
        if (JwtTokenInfo.IsFresh(_accessToken, ExpirySkew))
        {
            return _accessToken;
        }

        // Single-flight: concurrent callers (delegating handler, auth-state, SignalR) share one
        // acquisition. The lock is what makes it single: an unguarded "??=" lets two callers each
        // start a hydrate, and the later one to finish overwrites the other's token. HydrateAsync
        // reaches its first await immediately, so nothing slow runs under the lock.
        Task<string?> inFlight;
        lock (_hydrateSync)
        {
            _hydrateInFlight ??= HydrateAsync();
            inFlight = _hydrateInFlight;
        }

        try
        {
            return await inFlight.ConfigureAwait(false);
        }
        finally
        {
            // Only clear our own task: an unguarded clear can drop a NEWER hydrate started after
            // this one completed, splitting the next set of callers again.
            lock (_hydrateSync)
            {
                if (ReferenceEquals(_hydrateInFlight, inFlight))
                {
                    _hydrateInFlight = null;
                }
            }
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
