using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Maui.Services;

/// <summary>
/// MAUI token storage: the freshness-checking layer over <see cref="ISecureTokenStore"/>. Raw
/// persistence lives in <see cref="MauiSecureTokenStore"/> (OS SecureStorage); this type adds what a
/// long-lived mobile session needs, exactly as <c>WasmTokenStorageService</c> does for the browser.
/// A token read back from the enclave can be hours or days old, so returning it verbatim hands every
/// caller (the delegating handler, the auth-state provider, SignalR) an expired bearer and produces a
/// 401 the user experiences as a random sign-out. Reading through the expiry check instead refreshes
/// proactively, <see cref="ExpirySkew"/> ahead of the actual expiry.
/// <para>
/// Register with <c>AddCommonMauiTokenStorage()</c>, which wires the raw store alongside it; the
/// browser-host siblings are <c>WasmTokenStorageService</c> (MMCA.Common.UI) and
/// <c>ServerTokenStorageService</c> (MMCA.Common.UI.Web).
/// </para>
/// </summary>
public sealed class MauiTokenStorageService(
    ISecureTokenStore store,
    ITokenRefresher tokenRefresher) : ITokenStorageService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private readonly Lock _hydrateSync = new();

    private Task<string?>? _hydrateInFlight;

    /// <inheritdoc />
    public async Task<string?> GetAccessTokenAsync()
    {
        var stored = await store.GetAccessTokenAsync();
        if (JwtTokenInfo.IsFresh(stored, ExpirySkew))
        {
            return stored;
        }

        // Single-flight: concurrent callers (delegating handler, auth-state, SignalR) share one
        // acquisition. The lock is what makes it single: an unguarded "??=" lets two callers each
        // start a refresh, and every extra refresh rotates the refresh token again, invalidating the
        // pair the other caller is still holding. HydrateAsync reaches its first await immediately,
        // so nothing slow runs under the lock.
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

    /// <inheritdoc />
    public Task<string?> GetRefreshTokenAsync() => store.GetRefreshTokenAsync();

    /// <inheritdoc />
    public Task SetTokensAsync(string accessToken, string refreshToken) =>
        store.SetTokensAsync(accessToken, refreshToken);

    /// <inheritdoc />
    public Task ClearTokensAsync() => store.ClearTokensAsync();

    private async Task<string?> HydrateAsync() =>
        await tokenRefresher.AcquireAccessTokenAsync().ConfigureAwait(false);
}
