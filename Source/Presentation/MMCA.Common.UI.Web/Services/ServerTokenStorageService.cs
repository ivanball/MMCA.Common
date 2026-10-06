using Microsoft.AspNetCore.Http;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;

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
/// <para>
/// On the circuit, a visitor with no session is answered from memory for
/// <see cref="AnonymousGrace"/> after a hydrate comes back with a DEFINITIVE "no session" (see
/// <see cref="ISessionAwareTokenRefresher"/>), instead of one token round trip per API call; a
/// session created in another tab is seen within that window, and <see cref="SetTokensAsync"/> ends
/// it at once. A transient failure is never remembered: the next read hydrates again.
/// </para>
/// </summary>
/// <param name="httpContextAccessor">Tells the SSR prerender apart from the interactive circuit.</param>
/// <param name="cookieTokenReader">Reads the HttpOnly cookies during SSR.</param>
/// <param name="sessionCookieSync">Seeds and clears the HttpOnly session cookies.</param>
/// <param name="tokenRefresher">Acquires an access token from the session cookies on the circuit.</param>
/// <param name="timeProvider">The clock for the anonymous grace; <see cref="TimeProvider.System"/> when null.</param>
public sealed class ServerTokenStorageService(
    IHttpContextAccessor httpContextAccessor,
    CookieTokenReader cookieTokenReader,
    ISessionCookieSync sessionCookieSync,
    ITokenRefresher tokenRefresher,
    TimeProvider? timeProvider) : ITokenStorageService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    /// <summary>How long a "no session" answer is remembered on the circuit before the next hydrate.</summary>
    private static readonly TimeSpan AnonymousGrace = TimeSpan.FromSeconds(15);

    private readonly Lock _hydrateSync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private string? _accessToken;
    private Task<string?>? _hydrateInFlight;
    private DateTimeOffset _anonymousUntil;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerTokenStorageService"/> class on the system
    /// clock. Kept so code compiled against the original four-argument constructor keeps binding.
    /// </summary>
    /// <param name="httpContextAccessor">Tells the SSR prerender apart from the interactive circuit.</param>
    /// <param name="cookieTokenReader">Reads the HttpOnly cookies during SSR.</param>
    /// <param name="sessionCookieSync">Seeds and clears the HttpOnly session cookies.</param>
    /// <param name="tokenRefresher">Acquires an access token from the session cookies on the circuit.</param>
    public ServerTokenStorageService(
        IHttpContextAccessor httpContextAccessor,
        CookieTokenReader cookieTokenReader,
        ISessionCookieSync sessionCookieSync,
        ITokenRefresher tokenRefresher)
        : this(httpContextAccessor, cookieTokenReader, sessionCookieSync, tokenRefresher, timeProvider: null)
    {
    }

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

        if (_accessToken is null && _timeProvider.GetUtcNow() < _anonymousUntil)
        {
            return null;
        }

        // Single-flight: concurrent callers (delegating handler, auth-state, SignalR) share one
        // acquisition. The circuit is genuinely multi-threaded, so an unguarded "??=" lets two
        // callers each start a hydrate and the later one to finish overwrite the other's token.
        // HydrateAsync reaches its first await immediately, so nothing slow runs under the lock.
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

    public Task<string?> GetRefreshTokenAsync()
    {
        // The refresh token is never held client-side; SSR can read the cookie, the circuit cannot (HttpOnly).
        var refreshToken = httpContextAccessor.HttpContext is not null ? cookieTokenReader.ReadRefreshToken() : null;
        return Task.FromResult(refreshToken);
    }

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
