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
/// <para>
/// When that definitive answer comes for a session this client HELD (revoked from another device, or
/// the account deleted), <see cref="SessionEnded"/> fires so the auth state signs the shell out (A-34).
/// </para>
/// </summary>
/// <param name="sessionCookieSync">Seeds and clears the HttpOnly session cookies.</param>
/// <param name="tokenRefresher">Acquires an access token from the session cookies.</param>
/// <param name="timeProvider">The clock for the anonymous grace; <see cref="TimeProvider.System"/> when null.</param>
public sealed class WasmTokenStorageService(
    ISessionCookieSync sessionCookieSync,
    ITokenRefresher tokenRefresher,
    TimeProvider? timeProvider) : ISessionAwareTokenStorage
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
    /// Bumped by every <see cref="SetTokensAsync"/>, and by <see cref="ClearTokensAsync"/> both before
    /// and after its cookie clear. A hydrate that sees it move while its acquisition was in flight
    /// discards its stale answer (M234).
    /// </summary>
    private int _tokenVersion;

    /// <summary>
    /// Whether this client holds a session: set by a login and by every hydrate that acquired a token,
    /// cleared by a logout and when the session is found ended. A transient failure leaves it alone,
    /// so a definitive answer that follows one still counts as the end of a held session.
    /// </summary>
    private volatile bool _sessionHeld;

    /// <inheritdoc />
    public event EventHandler? SessionEnded;

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
        Interlocked.Increment(ref _tokenVersion);
        _anonymousUntil = default;
        _accessToken = accessToken;
        _sessionHeld = true;
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
        _sessionHeld = false;
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
        // Only a session-aware refresher can say "no session" for certain; a plain refresher's null is
        // read as no session for the grace, but is not proof enough to sign a held session out.
        var version = Volatile.Read(ref _tokenVersion);
        string? acquired;
        var unavailable = false;
        var definitiveNoSession = false;
        if (tokenRefresher is ISessionAwareTokenRefresher sessionAware)
        {
            var acquisition = await sessionAware.TryAcquireAccessTokenAsync().ConfigureAwait(false);
            acquired = acquisition.AccessToken;
            unavailable = acquisition.IsUnavailable;
            definitiveNoSession = acquired is null && !unavailable;
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

        if (_accessToken is null && !unavailable)
        {
            _anonymousUntil = _timeProvider.GetUtcNow() + AnonymousGrace;
        }

        if (_accessToken is not null)
        {
            _sessionHeld = true;
        }
        else if (definitiveNoSession && _sessionHeld)
        {
            // The session this client held ended on the server (A-34): the token is already gone
            // from memory, and the auth state is told so the shell signs out.
            _sessionHeld = false;
            SessionEnded?.Invoke(this, EventArgs.Empty);
        }

        return _accessToken;
    }
}
