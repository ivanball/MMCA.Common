namespace MMCA.Common.UI.Services.Capabilities.DeviceStorage;

/// <summary>
/// Small JSON document cache on the device (offline schedule snapshots). MAUI persists to
/// the app data directory, WebAssembly to <c>localStorage</c>; the Blazor Server fallback is
/// unavailable (SSR always has the live API). Not for secrets and not a query cache — this
/// is last-known-good UI state for offline rendering.
/// </summary>
public interface ILocalCacheStore
{
    /// <summary>Whether cached values survive restarts on this host.</summary>
    bool IsAvailable { get; }

    /// <summary>Serializes and stores <paramref name="value"/> under <paramref name="key"/>. Best-effort.</summary>
    /// <typeparam name="T">Any JSON-serializable document type.</typeparam>
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    /// <summary>Reads and deserializes the value stored under <paramref name="key"/>, or <see langword="default"/>.</summary>
    /// <typeparam name="T">Any JSON-serializable document type.</typeparam>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes the entry under <paramref name="key"/>; unknown keys are ignored.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every entry this store holds.
    /// <para>
    /// SECURITY: this is what sign-out calls. Snapshots are written in plaintext and survive process
    /// restarts, and their keys identify a surface rather than a user, so leaving them in place
    /// hands the next account on the device the previous account's rows the first time the network
    /// is unavailable. The default implementation is a no-op so an existing custom store still
    /// compiles; every store that actually persists anything must override it.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
