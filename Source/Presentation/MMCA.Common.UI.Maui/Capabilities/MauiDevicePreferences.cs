using System.Text.Json;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IDevicePreferences"/> over <c>Preferences.Default</c>. Values are stored
/// JSON-encoded under a shared prefix, mirroring the browser implementation, so the same
/// key/value semantics hold on every head. Never store secrets here — those belong in
/// <c>SecureStorage</c>.
/// </summary>
public sealed class MauiDevicePreferences : IDevicePreferences
{
    private const string KeyPrefix = "mmca.devicePrefs.";

    /// <inheritdoc />
    public bool IsPersistent => true;

    /// <inheritdoc />
    public Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var raw = Preferences.Default.Get<string?>(KeyPrefix + key, null);
        if (raw is null)
        {
            return Task.FromResult(fallback);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(raw);
            return Task.FromResult(value is null ? fallback : value);
        }
        catch (JsonException)
        {
            return Task.FromResult(fallback);
        }
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Preferences.Default.Set(KeyPrefix + key, JsonSerializer.Serialize(value));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Preferences.Default.Remove(KeyPrefix + key);
        return Task.CompletedTask;
    }
}
