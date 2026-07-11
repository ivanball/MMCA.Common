namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Per-device settings for hardware behaviors (reminder lead time, haptics toggle, app-lock).
/// Distinct from the server-side per-user preferences (culture/theme via
/// <c>IUserPreferenceWriter</c>): device preferences describe THIS device and never roam.
/// Never store secrets here — tokens belong in the platform secure storage.
/// Supported value types: <see cref="string"/>, <see cref="bool"/>, <see cref="int"/>,
/// <see cref="long"/>, <see cref="double"/>, and <see cref="DateTimeOffset"/>.
/// </summary>
public interface IDevicePreferences
{
    /// <summary>
    /// Whether values survive an app restart. The Blazor Server fallback is in-memory only
    /// (<see langword="false"/>) and hosts hide device-settings UI when not persistent.
    /// </summary>
    bool IsPersistent { get; }

    /// <summary>Reads a value, returning <paramref name="fallback"/> when absent or unreadable.</summary>
    /// <typeparam name="T">One of the supported value types listed on the interface.</typeparam>
    Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default);

    /// <summary>Writes a value. Best-effort: storage failures are swallowed.</summary>
    /// <typeparam name="T">One of the supported value types listed on the interface.</typeparam>
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    /// <summary>Removes a stored value; unknown keys are ignored.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
