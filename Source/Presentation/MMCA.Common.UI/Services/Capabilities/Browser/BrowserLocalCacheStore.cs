using System.Text.Json;

namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Browser <see cref="ILocalCacheStore"/> over <c>localStorage</c> (JSON documents,
/// <c>mmca.localCache.</c> key prefix). Suits small offline snapshots; browsers cap
/// <c>localStorage</c> around 5 MB, so callers keep documents lean.
/// </summary>
public sealed class BrowserLocalCacheStore : ILocalCacheStore
{
    private const string KeyPrefix = "mmca.localCache.";

    private readonly CapabilitiesJsModule _module;

    /// <summary>Initializes the store over the shared capabilities JS module.</summary>
    public BrowserLocalCacheStore(CapabilitiesJsModule module) => _module = module;

    /// <inheritdoc />
    public bool IsAvailable => true;

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
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var raw = await _module
            .InvokeOrDefaultAsync<string?>("storageGet", [KeyPrefix + key], cancellationToken)
            .ConfigureAwait(false);
        if (raw is null)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch (JsonException)
        {
            return default;
        }
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
