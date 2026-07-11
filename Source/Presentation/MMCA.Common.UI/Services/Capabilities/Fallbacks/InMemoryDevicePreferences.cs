using System.Collections.Concurrent;

namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>
/// Default <see cref="IDevicePreferences"/>: in-memory, non-persistent (values live for the
/// scope's lifetime only). Hosts consult <see cref="IsPersistent"/> to hide device-settings
/// UI where preferences would not survive a restart.
/// </summary>
public sealed class InMemoryDevicePreferences : IDevicePreferences
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool IsPersistent => false;

    /// <inheritdoc />
    public Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _values.TryGetValue(key, out var stored) && stored is T typed
            ? Task.FromResult(typed)
            : Task.FromResult(fallback);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _values[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
