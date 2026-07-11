using System.Text.Json;

namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Browser <see cref="IDevicePreferences"/> over <c>localStorage</c> (JSON-encoded values,
/// <c>mmca.devicePrefs.</c> key prefix). Persistent across sessions on the same browser
/// profile; storage failures degrade to the provided fallbacks.
/// </summary>
public sealed class BrowserDevicePreferences : IDevicePreferences
{
    private const string KeyPrefix = "mmca.devicePrefs.";

    private readonly CapabilitiesJsModule _module;

    /// <summary>Initializes the store over the shared capabilities JS module.</summary>
    public BrowserDevicePreferences(CapabilitiesJsModule module) => _module = module;

    /// <inheritdoc />
    public bool IsPersistent => true;

    /// <inheritdoc />
    public async Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var raw = await _module
            .InvokeOrDefaultAsync<string?>("storageGet", [KeyPrefix + key], cancellationToken)
            .ConfigureAwait(false);
        if (raw is null)
        {
            return fallback;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(raw);
            return value is null ? fallback : value;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var raw = JsonSerializer.Serialize(value);
        await _module
            .InvokeOrDefaultAsync<bool?>("storageSet", [KeyPrefix + key, raw], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _module
            .InvokeOrDefaultAsync<bool?>("storageRemove", [KeyPrefix + key], cancellationToken)
            .ConfigureAwait(false);
    }
}
