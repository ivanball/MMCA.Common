using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Primitives;

namespace MMCA.Common.Infrastructure.Tests.Caching;

/// <summary>
/// A real <see cref="MemoryCache"/> on a manual clock that tells a test when post-eviction callbacks
/// have run. <see cref="IMemoryCache"/> queues those callbacks to the thread pool, so a test that
/// asserts on their effect has to wait for them; waiting on this signal replaces a fixed delay that
/// was only ever a guess at how long the pool would take.
/// </summary>
/// <remarks>
/// Each entry gets one extra callback, registered when the entry is committed and therefore AFTER
/// every callback the code under test registered. <see cref="MemoryCache"/> runs an entry's
/// callbacks in registration order on one work item, so the signal fires only once the code under
/// test's own callback has returned.
/// </remarks>
internal sealed class EvictionSignalingMemoryCache : IMemoryCache
{
    private readonly ManualClock _clock = new();
    private readonly SemaphoreSlim _evictions = new(0);
    private readonly MemoryCache _inner;

    public EvictionSignalingMemoryCache() =>
        _inner = new MemoryCache(new MemoryCacheOptions { Clock = _clock });

    /// <summary>Moves the cache's clock forward, so entries past their expiration expire on next access.</summary>
    /// <param name="by">How far to advance.</param>
    public void AdvanceClock(TimeSpan by) => _clock.UtcNow += by;

    /// <summary>Waits until <paramref name="count"/> more entries have finished their eviction callbacks.</summary>
    /// <param name="count">How many evictions to wait for.</param>
    /// <returns>A task that completes when they have; it faults if they do not within five seconds each.</returns>
    public async Task WaitForEvictionCallbacksAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!await _evictions.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"Only {i} of {count} eviction callbacks ran within the timeout."));
            }
        }
    }

    /// <inheritdoc />
    public ICacheEntry CreateEntry(object key) => new SignalingEntry(_inner.CreateEntry(key), _evictions);

    /// <inheritdoc />
    public void Remove(object key) => _inner.Remove(key);

    /// <inheritdoc />
    public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Dispose();
        _evictions.Dispose();
    }

    private sealed class ManualClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Delegates to the real entry and appends the signal callback when it is committed.</summary>
    private sealed class SignalingEntry(ICacheEntry inner, SemaphoreSlim evictions) : ICacheEntry
    {
        public object Key => inner.Key;

        public object? Value
        {
            get => inner.Value;
            set => inner.Value = value;
        }

        public DateTimeOffset? AbsoluteExpiration
        {
            get => inner.AbsoluteExpiration;
            set => inner.AbsoluteExpiration = value;
        }

        public TimeSpan? AbsoluteExpirationRelativeToNow
        {
            get => inner.AbsoluteExpirationRelativeToNow;
            set => inner.AbsoluteExpirationRelativeToNow = value;
        }

        public TimeSpan? SlidingExpiration
        {
            get => inner.SlidingExpiration;
            set => inner.SlidingExpiration = value;
        }

        public IList<IChangeToken> ExpirationTokens => inner.ExpirationTokens;

        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => inner.PostEvictionCallbacks;

        public CacheItemPriority Priority
        {
            get => inner.Priority;
            set => inner.Priority = value;
        }

        public long? Size
        {
            get => inner.Size;
            set => inner.Size = value;
        }

        public void Dispose()
        {
            inner.RegisterPostEvictionCallback((_, _, _, _) => evictions.Release());
            inner.Dispose();
        }
    }
}
