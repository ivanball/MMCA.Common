using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Maui.Services;

/// <summary>
/// MAUI token storage using <see cref="SecureStorage"/>, which leverages platform-specific
/// secure enclaves (Android Keystore, iOS Keychain, Windows DPAPI) to protect tokens at rest.
/// Every call is guarded: the OS invalidates keystore entries on its own schedule (an Android
/// backup/restore onto a new device, a security patch that rotates the master key, a biometric
/// enrolment change), and the raw APIs then throw a platform exception instead of returning
/// nothing. An unhandled throw here bricks the app on launch until it is reinstalled, so a read
/// failure degrades to "no token stored" (which <see cref="ITokenStorageService"/> already
/// documents) and drops the unreadable entry, forcing one clean re-login.
/// <para>
/// Register with <c>AddCommonMauiTokenStorage()</c>; the browser-host siblings are
/// <c>WasmTokenStorageService</c> (MMCA.Common.UI) and <c>ServerTokenStorageService</c>
/// (MMCA.Common.UI.Web).
/// </para>
/// </summary>
public sealed class MauiTokenStorageService : ITokenStorageService
{
    private const string AccessTokenKey = "auth_access_token";
    private const string RefreshTokenKey = "auth_refresh_token";

    /// <inheritdoc />
    public Task<string?> GetAccessTokenAsync() => GetAsync(AccessTokenKey);

    /// <inheritdoc />
    public Task<string?> GetRefreshTokenAsync() => GetAsync(RefreshTokenKey);

    /// <inheritdoc />
    public async Task SetTokensAsync(string accessToken, string refreshToken)
    {
        await SetAsync(AccessTokenKey, accessToken);
        await SetAsync(RefreshTokenKey, refreshToken);
    }

    /// <inheritdoc />
    public Task ClearTokensAsync()
    {
        // Logout must always succeed: an entry we cannot delete is one the OS already
        // invalidated, which is the outcome the caller asked for.
        TryRemove(AccessTokenKey);
        TryRemove(RefreshTokenKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads one entry, degrading a corrupt or undecryptable entry to <see langword="null"/> and
    /// dropping it so the next write starts from a clean key.
    /// </summary>
    private static async Task<string?> GetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
#pragma warning disable CA1031 // Do not catch general exception types - the platform exception type differs per OS and none of them are recoverable here
        catch
#pragma warning restore CA1031
        {
            // No logging facility exists in this host (nothing in the MAUI head takes an ILogger),
            // so the swallow is documented here rather than reported: the user sees the normal
            // signed-out state and logs in again.
            TryRemove(key);
            return null;
        }
    }

    /// <summary>
    /// Writes one entry, retrying once against a freshly removed key. A second failure propagates:
    /// the caller must never believe a token was persisted when it was not.
    /// </summary>
    private static async Task SetAsync(string key, string value)
    {
        try
        {
            await SecureStorage.Default.SetAsync(key, value);
        }
#pragma warning disable CA1031 // Do not catch general exception types - see GetAsync; the retry below is the recovery
        catch
#pragma warning restore CA1031
        {
            TryRemove(key);
            await SecureStorage.Default.SetAsync(key, value);
        }
    }

    /// <summary>Best-effort delete of one entry; a delete that itself throws is already the goal.</summary>
    private static void TryRemove(string key)
    {
        try
        {
            SecureStorage.Default.Remove(key);
        }
#pragma warning disable CA1031 // Do not catch general exception types - see GetAsync
        catch
#pragma warning restore CA1031
        {
            // Nothing left to do: the entry is unreadable and undeletable, and the next
            // SetAsync overwrites it anyway.
        }
    }
}
