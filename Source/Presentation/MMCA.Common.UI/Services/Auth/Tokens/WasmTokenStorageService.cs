namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// WebAssembly token storage (cookie-only, no localStorage). The access token lives <b>in memory only</b>
/// and is hydrated/refreshed on demand from the HttpOnly cookies via the same-origin
/// <c>/auth/session/token</c> endpoint (<see cref="ITokenRefresher"/>). The refresh token is never readable
/// by JS. <see cref="SetTokensAsync"/> seeds the cookies at login via <see cref="ISessionCookieSync"/>.
/// Hoisted from the app WASM clients (it carries no app-specific state); its Blazor Server sibling is
/// <c>ServerTokenStorageService</c> in MMCA.Common.UI.Web.
/// <para>
/// A visitor with no session is answered from memory for <see cref="AnonymousGrace"/> after a
/// hydrate comes back with a DEFINITIVE "no session" (see <see cref="ISessionAwareTokenRefresher"/>),
/// instead of one same-origin token POST per API call; a session created in another tab is
/// therefore seen within that window, and <see cref="SetTokensAsync"/> (a login here) ends it at
/// once. A transient failure (a 429, a 5xx, a dropped connection) is never remembered: the next read
/// hydrates again.
/// </para>
/// </summary>
/// <param name="sessionCookieSync">Seeds and clears the HttpOnly session cookies.</param>
/// <param name="tokenRefresher">Acquires an access token from the session cookies.</param>
/// <param name="timeProvider">The clock for the anonymous grace; <see cref="TimeProvider.System"/> when null.</param>
public sealed class WasmTokenStorageService(
    ISessionCookieSync sessionCookieSync,
    ITokenRefresher tokenRefresher,
    TimeProvider? timeProvider) : ITokenStorageService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    /// <summary>How long a "no session" answer is remembered before the next hydrate.</summary>
    private static readonly TimeSpan AnonymousGrace = TimeSpan.FromSeconds(15);

    private readonly Lock _hydrateSync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private string? _accessToken;
    private Task<string?>? _hydrateInFlight;
    private DateTimeOffset _anonymousUntil;

    /// <summary>
    /// Initializes a new instance of the <see cref="WasmTokenStorageService"/> class on the system
    /// clock. Kept so code compiled against the original two-argument constructor keeps binding.
    /// </summary>
    /// <param name="sessionCookieSync">Seeds and clears the HttpOnly session cookies.</param>
    /// <param name="tokenRefresher">Acquires an access token from the session cookies.</param>
    public WasmTokenStorageService(ISessionCookieSync sessionCookieSync, ITokenRefresher tokenRefresher)
        : this(sessionCookieSync, tokenRefresher, timeProvider: null)
    {
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        if (JwtTokenInfo.IsFresh(_accessToken, ExpirySkew))
        {
            return _accessToken;
        }

        if (_accessToken is null && _timeProvider.GetUtcNow() < _anonymousUntil)
        {
            return null;
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

    // The refresh token is never held client-side in the browser: it lives only in the HttpOnly cookie.
    public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>(null);

    public async Task SetTokensAsync(string accessToken, string refreshToken)
    {
        // A login right after anonymous browsing must not wait out the grace.
        _anonymousUntil = default;
        _accessToken = accessToken;
        // Seed the HttpOnly cookies at login. The refresh token transits JS only for this same-origin POST
        // and is never persisted in localStorage. A failed write is surfaced (after the in-memory token is
        // set) so AuthUIService reports Auth.TokenStorageUnavailable instead of a login that silently signs
        // out at the first access-token expiry, when no cookie exists to refresh from.
        if (!await sessionCookieSync.SyncAsync(accessToken, refreshToken))
        {
            throw new InvalidOperationException("The session cookie could not be written.");
        }
    }

    public async Task ClearTokensAsync()
    {
        _accessToken = null;
        // Best-effort by contract (IAuthUIService.LogoutAsync never fails), so a false is ignored. A caller
        // that needs proof the session ended uses IAuthUIService.RevokeAllSessionsAsync, which reports the
        // server-side revoke.
        _ = await sessionCookieSync.ClearAsync();
    }

    private async Task<string?> HydrateAsync()
    {
        // Only a DEFINITIVE "no session" starts the grace. A refresher that can tell (the browser
        // ones) reports a 429, a 5xx, a dropped connection or unavailable interop as unavailable, and
        // the next read then hydrates again instead of treating a signed-in user as anonymous. A
        // refresher without that capability keeps the previous reading: its null means "no session".
        var unavailable = false;
        if (tokenRefresher is ISessionAwareTokenRefresher sessionAware)
        {
            var acquisition = await sessionAware.TryAcquireAccessTokenAsync().ConfigureAwait(false);
            _accessToken = acquisition.AccessToken;
            unavailable = acquisition.IsUnavailable;
        }
        else
        {
            _accessToken = await tokenRefresher.AcquireAccessTokenAsync().ConfigureAwait(false);
        }

        if (_accessToken is null && !unavailable)
        {
            _anonymousUntil = _timeProvider.GetUtcNow() + AnonymousGrace;
        }

        return _accessToken;
    }
}
