using System.Text.Json;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="ILocalCacheStore"/>: JSON documents in an <c>mmca-cache</c> folder under
/// the app data directory. Keys map to file names via a conservative character filter (keys
/// are code-controlled, not user input). All IO is best-effort.
/// </summary>
public sealed class MauiLocalCacheStore : ILocalCacheStore
{
    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var path = GetPath(key, ensureDirectory: true);
            var json = JsonSerializer.Serialize(value);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Best-effort cache: a failed write only means a colder next launch.
        }
        catch (UnauthorizedAccessException)
        {
            // Same disposition.
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var path = GetPath(key, ensureDirectory: false);
            if (!File.Exists(path))
            {
                return default;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            File.Delete(GetPath(key, ensureDirectory: false));
        }
        catch (IOException)
        {
            // Best-effort cache.
        }
        catch (UnauthorizedAccessException)
        {
            // Same disposition.
        }

        return Task.CompletedTask;
    }

    private static string GetPath(string key, bool ensureDirectory)
    {
        var directory = Path.Combine(FileSystem.AppDataDirectory, "mmca-cache");
        if (ensureDirectory)
        {
            Directory.CreateDirectory(directory);
        }

        var fileName = new string([.. key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);
        return Path.Combine(directory, fileName + ".json");
    }
}
