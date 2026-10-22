using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Web.Services;

/// <summary>
/// Blazor Server token storage (cookie-only, no localStorage). During SSR prerender (a live
/// <see cref="HttpContext"/>) the access token is read from the HttpOnly cookie — refreshed in place by
/// <c>UseCookieSessionRefresh</c> on navigations. On the interactive circuit (whose <see cref="HttpContext"/>
/// is only the stale request that opened the hub connection; see below) the access token is held <b>in memory only</b> and hydrated/refreshed on demand from the HttpOnly cookies
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
/// <para>
/// On the circuit <see cref="IHttpContextAccessor.HttpContext"/> is NOT null: it is the request that
/// opened the Blazor hub connection, sent before any sign-in on that circuit and never updated, so its
/// cookie is stale by construction. Two signals keep that cookie from answering for the circuit. Once
/// this scoped instance has set, cleared or hydrated a token it is a circuit instance (the cookie sync
/// and the refresher both need JS interop, which a prerender does not have, and the prerender and the
/// circuit never share a DI scope), and from then on memory is the source of truth. Before that, a hub
/// connection request (SignalR <see cref="HubMetadata"/> on its endpoint, or a WebSocket request) may
/// still answer from its cookie while that token is fresh (or absent, as for an anonymous visitor),
/// which is how a circuit starts, but an expired one is re-acquired through the refresher instead of
/// being returned. A real page request
/// (the SSR prerender) carries neither signal and reads the cookie exactly as before.
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
    /// Bumped by every <see cref="SetTokensAsync"/>, and by <see cref="ClearTokensAsync"/> both before
    /// and after its cookie clear. A hydrate that sees it move while its acquisition was in flight
    /// discards its stale answer (M234).
    /// </summary>
    private int _tokenVersion;

    /// <summary>
    /// Set once this instance has stored, cleared or hydrated a token, which only happens on the
    /// circuit: from then on the in-memory token wins over the connection request's stale cookie.
    /// </summary>
    private volatile bool _memoryIsAuthoritative;

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
        if (TryAnswerFromCookie(out var cookieToken))
        {
            return cookieToken;
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
        // The refresh token is never held client-side or in memory. It is read from the HttpOnly cookie of
        // the current HttpContext whenever one exists: the live request during SSR, and on the circuit the
        // hub-connection request, whose cookie is as stale as described in the class remarks.
        var refreshToken = httpContextAccessor.HttpContext is not null ? cookieTokenReader.ReadRefreshToken() : null;
        return Task.FromResult(refreshToken);
    }

    public async Task SetTokensAsync(string accessToken, string refreshToken)
    {
        // A login right after anonymous browsing must not wait out the grace.
        Interlocked.Increment(ref _tokenVersion);
        _anonymousUntil = default;
        _accessToken = accessToken;
        _memoryIsAuthoritative = true;
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
        Interlocked.Increment(ref _tokenVersion);
        _accessToken = null;
        // A logout must not be undone by the connection request's cookie, which still holds the token
        // the circuit started with: from here the next read re-acquires from the (cleared) session.
        _memoryIsAuthoritative = true;
        // Best-effort by contract (IAuthUIService.LogoutAsync never fails), so a false is ignored. A caller
        // that needs proof the session ended uses IAuthUIService.RevokeAllSessionsAsync, which reports the
        // server-side revoke.
        _ = await sessionCookieSync.ClearAsync();

        // Bump again once the cookies are gone (M234 review): a hydrate that STARTED during the clear
        // captured the first bump and read the session the clear was still removing. This discards its
        // answer if it is still in flight, and drops it if it already landed.
        Interlocked.Increment(ref _tokenVersion);
        _accessToken = null;
    }

    private async Task<string?> HydrateAsync()
    {
        // Only a DEFINITIVE "no session" starts the grace. A refresher that can tell (the browser
        // ones) reports a 429, a 5xx, a dropped connection or unavailable interop as unavailable, and
        // the next read then hydrates again instead of treating a signed-in user as anonymous. A
        // refresher without that capability keeps the previous reading: its null means "no session".
        // A login or logout that lands while the acquisition is in flight wins (M234): the answer was
        // read from the session as it was before, so it is discarded together with its grace.
        var version = Volatile.Read(ref _tokenVersion);
        string? acquired;
        var unavailable = false;
        if (tokenRefresher is ISessionAwareTokenRefresher sessionAware)
        {
            var acquisition = await sessionAware.TryAcquireAccessTokenAsync().ConfigureAwait(false);
            acquired = acquisition.AccessToken;
            unavailable = acquisition.IsUnavailable;
        }
        else
        {
            acquired = await tokenRefresher.AcquireAccessTokenAsync().ConfigureAwait(false);
        }

        if (Volatile.Read(ref _tokenVersion) != version)
        {
            return _accessToken;
        }

        _accessToken = acquired;

        _memoryIsAuthoritative = true;

        if (_accessToken is null && !unavailable)
        {
            _anonymousUntil = _timeProvider.GetUtcNow() + AnonymousGrace;
        }

        return _accessToken;
    }

    /// <summary>
    /// Answers from the request's HttpOnly cookie when that cookie, not memory, is the source of truth.
    /// </summary>
    /// <param name="token">The cookie's access token (null when absent) when this returns true.</param>
    /// <returns><see langword="false"/> when the in-memory path must answer instead.</returns>
    private bool TryAnswerFromCookie(out string? token)
    {
        token = null;
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null || _memoryIsAuthoritative)
        {
            return false;
        }

        token = cookieTokenReader.ReadAccessToken();

        // SSR prerender: the HttpOnly cookie (possibly just refreshed by the middleware / stashed in
        // HttpContext.Items) is the source of truth; JS interop is unavailable here.
        if (!IsHubConnection(httpContext))
        {
            return true;
        }

        // Circuit before any token was stored here: the connection request's cookie is the session the
        // circuit started with. No cookie answers anonymous as before and a fresh one is used, but an
        // expired one is never handed back (it would 401 every call for the life of the circuit): the
        // refresher re-acquires instead.
        return token is null || JwtTokenInfo.IsFresh(token, ExpirySkew);
    }

    /// <summary>
    /// Whether <paramref name="httpContext"/> is the request that opened the Blazor Server hub
    /// connection (any transport: the endpoint carries SignalR's <see cref="HubMetadata"/>; a WebSocket
    /// request is never a page render either) rather than a page request being prerendered.
    /// </summary>
    private static bool IsHubConnection(HttpContext httpContext) =>
        httpContext.GetEndpoint()?.Metadata.GetMetadata<HubMetadata>() is not null
        || httpContext.WebSockets.IsWebSocketRequest;
}
