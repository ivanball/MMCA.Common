using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Services.Caching;

/// <summary>
/// Default <see cref="IUiReadCache"/>: an in-memory dictionary keyed by the relative request URL,
/// guarded by a lock because circuit code is not single-threaded (a periodic poll, a SignalR push
/// handler and a user-driven page load can all reach the same instance).
/// </summary>
/// <remarks>
/// Expiry is lazy: an entry past its TTL is removed when it is next read, not by a timer. A UI cache
/// holds tens of entries for the life of a circuit, so a sweeping timer would cost more than the
/// entries it reclaims, and a stale entry that is never read again is never served either.
/// </remarks>
/// <param name="timeProvider">Clock used for the stored-at stamp and every freshness comparison.</param>
/// <param name="options">The staleness policy: enablement, default TTL, per-prefix overrides.</param>
internal sealed class UiReadCache(TimeProvider timeProvider, IOptions<UiReadCacheOptions> options) : IUiReadCache
{
    private readonly Lock _sync = new();

    // Keys are relative URLs, and a Dictionary<string, ...> compares them ordinally by default, which
    // is the comparison every prefix check below also uses: two URLs that differ only in case are two
    // different requests to the server, so they must be two different entries here.
    private readonly Dictionary<string, (object Value, DateTimeOffset StoredAt)> _entries = [];
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly UiReadCacheOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <inheritdoc />
    public bool TryGetFresh<T>(string url, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        value = default;

        if (!_options.Enabled)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (!_entries.TryGetValue(url, out var entry))
            {
                return false;
            }

            if (now - entry.StoredAt > ResolveTtl(url))
            {
                _entries.Remove(url);
                return false;
            }

            if (entry.Value is not T typed)
            {
                // The same URL read back as a different type means the caller changed shape; the
                // stored value can no longer answer the question, so drop it and let the read run.
                _entries.Remove(url);
                return false;
            }

            value = typed;
            return true;
        }
    }

    /// <inheritdoc />
    public void Set<T>(string url, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!_options.Enabled || value is null)
        {
            return;
        }

        var storedAt = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            _entries[url] = (value, storedAt);
        }
    }

    /// <inheritdoc />
    public void InvalidatePrefix(string routePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);

        lock (_sync)
        {
            var doomed = _entries.Keys
                .Where(key => key.StartsWith(routePrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var key in doomed)
            {
                _entries.Remove(key);
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// The freshness budget for one URL: the TTL of the LONGEST configured route prefix it starts
    /// with, or <see cref="UiReadCacheOptions.DefaultTtl"/> when it matches none. Longest-match rather
    /// than first-match so a nested route can state a stricter budget than the endpoint above it,
    /// whatever order the configuration happens to enumerate in.
    /// </summary>
    private TimeSpan ResolveTtl(string url)
    {
        var ttl = _options.DefaultTtl;
        var matchedLength = -1;

        foreach (var (prefix, prefixTtl) in _options.RoutePrefixTtls)
        {
            if (prefix.Length > matchedLength && url.StartsWith(prefix, StringComparison.Ordinal))
            {
                matchedLength = prefix.Length;
                ttl = prefixTtl;
            }
        }

        return ttl;
    }
}
