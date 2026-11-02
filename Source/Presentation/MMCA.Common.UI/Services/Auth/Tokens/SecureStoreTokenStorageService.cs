namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// Token storage over a persistent <see cref="ISecureTokenStore"/> (the MAUI head's OS SecureStorage):
/// the freshness-checking layer that a long-lived device session needs, as
/// <see cref="WasmTokenStorageService"/> is for the browser. A token read back from the store can be
/// hours or days old, so returning it verbatim would hand every caller (the delegating handler, the
/// auth-state provider, SignalR) an expired bearer; reading through the expiry check refreshes
/// proactively, <see cref="ExpirySkew"/> ahead of the actual expiry.
/// <para>
/// <b>A definitive "no session" ends the session here.</b> When the refresher reports
/// <see cref="TokenAcquisition.NoSession"/> for a pair this device held (the session was revoked from
/// another device, swept by reuse detection, or the account was deleted), the dead pair is cleared
/// from the store and <see cref="SessionEnded"/> fires so the auth state signs the shell out. Without
/// that, the dead pair stayed stored and every read re-posted a refresh the server could only refuse:
/// the 2026-10-17 conference-day loop of 6 to 31 refresh 401s a minute per device, with the app
/// looking signed in and nothing working. A transient failure
/// (<see cref="TokenAcquisition.Unavailable"/>, a 409 superseded included) keeps the pair for the next
/// attempt. Once cleared, the next read finds no pair and the refresher answers without a request.
/// </para>
/// <para>
/// The refresh must never read this storage again: <c>DirectApiTokenRefresher</c> posts through the
/// <c>APIClient</c> pipeline, whose <c>AuthDelegatingHandler</c> would otherwise ask this type for a
/// bearer and await the very hydrate that is waiting for the refresh, so that request opts out with
/// <c>AuthDelegatingHandler.SkipBearer</c>.
/// </para>
/// <para>
/// The refresh single-flight, the token version and the session-ended signal are process-wide
/// (static), not per instance: the service is scoped, and the <c>AuthDelegatingHandler</c> built by
/// <c>IHttpClientFactory</c> resolves its own instance in its handler scope, so a per-instance slot
/// let the handler scope and the component scope each rotate the same refresh token (the second
/// rotation reads as reuse and revokes the session, BR-206), and a per-instance event would fire on
/// the handler's instance while the auth state listens on the component's. A device runs one user
/// session per process, so one slot is the correct scope. Each instance removes the handlers it added
/// to the shared event when its scope disposes it.
/// </para>
/// </summary>
/// <param name="store">The persistent store holding both tokens.</param>
/// <param name="tokenRefresher">Acquires a fresh access token from the stored refresh token.</param>
public sealed class SecureStoreTokenStorageService(
    ISecureTokenStore store,
    ITokenRefresher tokenRefresher) : ISessionAwareTokenStorage, IDisposable
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private static readonly Lock HydrateSync = new();

    private static readonly Lock SessionEndedSync = new();

    private static Task<string?>? _hydrateInFlight;

    /// <summary>
    /// Bumped by every <see cref="SetTokensAsync"/> and <see cref="ClearTokensAsync"/>. A hydrate that
    /// sees it move while its acquisition was in flight leaves the store alone: a sign-in that landed
    /// mid-refresh wins over the "no session" answer read from the session before it (M234).
    /// </summary>
    private static int _tokenVersion;

    private static EventHandler? _processSessionEnded;

    private readonly List<EventHandler> _subscriptions = [];

    /// <inheritdoc />
    public event EventHandler? SessionEnded
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (SessionEndedSync)
            {
                Subscribe(value);
                _subscriptions.Add(value);
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            lock (SessionEndedSync)
            {
                Unsubscribe(value);
                _subscriptions.Remove(value);
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetAccessTokenAsync()
    {
        var stored = await store.GetAccessTokenAsync();
        if (JwtTokenInfo.IsFresh(stored, ExpirySkew))
        {
            return stored;
        }

        // Single-flight across every instance in the process (see the class remarks): concurrent
        // callers (delegating handler, auth-state, SignalR) share one acquisition. The lock is what
        // makes it single: an unguarded "??=" lets two callers each start a refresh, and every extra
        // refresh rotates the refresh token again, invalidating the pair the other caller is still
        // holding. HydrateAsync reaches its first await immediately, so nothing slow runs under the
        // lock.
        Task<string?> inFlight = JoinOrStartHydrate(HydrateAsync);

        try
        {
            return await inFlight.ConfigureAwait(false);
        }
        finally
        {
            ClearHydrateIfCurrent(inFlight);
        }
    }

    /// <inheritdoc />
    public Task<string?> GetRefreshTokenAsync() => store.GetRefreshTokenAsync();

    /// <inheritdoc />
    public Task SetTokensAsync(string accessToken, string refreshToken)
    {
        Interlocked.Increment(ref _tokenVersion);
        return store.SetTokensAsync(accessToken, refreshToken);
    }

    /// <inheritdoc />
    public Task ClearTokensAsync()
    {
        Interlocked.Increment(ref _tokenVersion);
        return store.ClearTokensAsync();
    }

    /// <summary>Removes the session-ended handlers this instance added to the process-wide event.</summary>
    public void Dispose()
    {
        lock (SessionEndedSync)
        {
            foreach (var handler in _subscriptions)
            {
                Unsubscribe(handler);
            }

            _subscriptions.Clear();
        }
    }

    // The process-wide handler list is written only here, from static members (Sonar S2696); every
    // caller already holds SessionEndedSync.
    private static void Subscribe(EventHandler handler) => _processSessionEnded += handler;

    private static void Unsubscribe(EventHandler handler) => _processSessionEnded -= handler;

    /// <summary>Joins the process-wide hydrate in flight, or starts one with <paramref name="start"/>.</summary>
    private static Task<string?> JoinOrStartHydrate(Func<Task<string?>> start)
    {
        lock (HydrateSync)
        {
            return _hydrateInFlight ??= start();
        }
    }

    /// <summary>
    /// Clears the slot only when it still holds <paramref name="inFlight"/>: an unguarded clear can
    /// drop a NEWER hydrate started after this one completed, splitting the next set of callers again.
    /// </summary>
    private static void ClearHydrateIfCurrent(Task<string?> inFlight)
    {
        lock (HydrateSync)
        {
            if (ReferenceEquals(_hydrateInFlight, inFlight))
            {
                _hydrateInFlight = null;
            }
        }
    }

    private static void RaiseSessionEnded(object sender)
    {
        EventHandler? handlers;
        lock (SessionEndedSync)
        {
            handlers = _processSessionEnded;
        }

        handlers?.Invoke(sender, EventArgs.Empty);
    }

    private async Task<string?> HydrateAsync()
    {
        if (tokenRefresher is not ISessionAwareTokenRefresher sessionAware)
        {
            // A refresher that cannot tell "no session" from "unavailable" keeps the previous
            // behavior: its null is passed through and the stored pair is left alone.
            return await tokenRefresher.AcquireAccessTokenAsync().ConfigureAwait(false);
        }

        var version = Volatile.Read(ref _tokenVersion);
        var heldPair = !string.IsNullOrWhiteSpace(await store.GetRefreshTokenAsync().ConfigureAwait(false));

        var acquisition = await sessionAware.TryAcquireAccessTokenAsync().ConfigureAwait(false);
        if (acquisition.AccessToken is not null || acquisition.IsUnavailable || !heldPair)
        {
            return acquisition.AccessToken;
        }

        if (Volatile.Read(ref _tokenVersion) != version)
        {
            // A sign-in or sign-out landed while the refresh was in flight; its pair, not this
            // answer about the previous one, is what the store now holds.
            return null;
        }

        await store.ClearTokensAsync().ConfigureAwait(false);
        Interlocked.Increment(ref _tokenVersion);
        RaiseSessionEnded(this);
        return null;
    }
}
