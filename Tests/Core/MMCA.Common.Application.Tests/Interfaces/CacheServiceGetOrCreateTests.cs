using System.Collections.Concurrent;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Concurrency;

namespace MMCA.Common.Application.Tests.Interfaces;

/// <summary>
/// Covers the DEFAULT implementation of
/// <see cref="ICacheService.GetOrCreateAsync{T}(string, Func{CancellationToken, Task{T}}, TimeSpan?, CancellationToken)"/>,
/// the one every backing store inherits unless it overrides the member.
/// <para>
/// The subject is a hand-written fake rather than a mock: a mocking proxy supplies its own body for
/// an interface member, which would test the proxy instead of the default implementation under test.
/// </para>
/// </summary>
public sealed class CacheServiceGetOrCreateTests
{
    /// <summary>Guards the choreographed tests against hanging the run if the stripe misbehaves.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // ── Hit path ──
    [Fact]
    public async Task GetOrCreateAsync_WhenTheKeyIsCached_ReturnsItWithoutInvokingTheFactory()
    {
        var recorder = new RecordingCacheService();
        ICacheService cache = recorder;
        await cache.SetAsync("k", "cached", cancellationToken: TestContext.Current.CancellationToken);

        var factoryCalls = 0;
        var result = await cache.GetOrCreateAsync<string>(
            "k",
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult("fresh");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("cached");
        factoryCalls.Should().Be(0);
        recorder.Sets.Should().ContainSingle("a hit must not rewrite the entry it just read");
    }

    // ── Miss path ──
    [Fact]
    public async Task GetOrCreateAsync_OnAMiss_InvokesTheFactoryAndCachesTheResult()
    {
        var recorder = new RecordingCacheService();
        ICacheService cache = recorder;

        var result = await cache.GetOrCreateAsync(
            "k",
            _ => Task.FromResult("fresh"),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("fresh");
        (await cache.GetAsync<string>("k", TestContext.Current.CancellationToken)).Should().Be("fresh");
    }

    [Fact]
    public async Task GetOrCreateAsync_ForwardsTheExpirationToSetAsync()
    {
        var recorder = new RecordingCacheService();
        ICacheService cache = recorder;
        var expiration = TimeSpan.FromMinutes(7);

        await cache.GetOrCreateAsync(
            "k",
            _ => Task.FromResult("fresh"),
            expiration,
            TestContext.Current.CancellationToken);

        var write = recorder.Sets.Should().ContainSingle().Subject;
        write.Key.Should().Be("k");
        write.Expiration.Should().Be(expiration);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithNoExpiration_LetsTheStoreApplyItsDefault()
    {
        var recorder = new RecordingCacheService();
        ICacheService cache = recorder;

        await cache.GetOrCreateAsync(
            "k",
            _ => Task.FromResult("fresh"),
            cancellationToken: TestContext.Current.CancellationToken);

        var write = recorder.Sets.Should().ContainSingle().Subject;
        write.Key.Should().Be("k");
        write.Expiration.Should().BeNull("the store applies its own default when the caller supplies none");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithoutAFactory_Throws()
    {
        var recorder = new RecordingCacheService();
        ICacheService cache = recorder;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            cache.GetOrCreateAsync<string>("k", factory: null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Stampede protection ──
    [Fact]
    public async Task GetOrCreateAsync_WithConcurrentMissesOnOneKey_InvokesTheFactoryOnce()
    {
        var recorder = new RecordingCacheService();
        ICacheService cache = recorder;
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;

        async Task<string> FactoryAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCalls);
            factoryEntered.TrySetResult();
            await releaseFactory.Task.WaitAsync(Timeout, ct);
            return "fresh";
        }

        var first = cache.GetOrCreateAsync<string>("hot", FactoryAsync, cancellationToken: TestContext.Current.CancellationToken);

        // The first caller now holds the stripe with the key still absent, which is precisely the
        // window a stampede would drive a second handler execution through.
        await factoryEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var second = cache.GetOrCreateAsync<string>("hot", FactoryAsync, cancellationToken: TestContext.Current.CancellationToken);

        releaseFactory.SetResult();

        var results = await Task.WhenAll(first, second).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        results.Should().AllBe("fresh");
        factoryCalls.Should().Be(1, "the waiter must be served the freshly cached entry, not run the factory again");
        recorder.Sets.Should().ContainSingle();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithMissesOnDistinctKeys_DoesNotSerializeThem()
    {
        var recorder = new RecordingCacheService();
        ICacheService cache = recorder;
        (string First, string Second) keys = DistinctStripeKeys();

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocked = cache.GetOrCreateAsync<string>(
            keys.First,
            async ct =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(Timeout, ct);
                return "first";
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await firstEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // A different key takes a different stripe, so this must complete while the first caller is
        // still inside its factory. If the lock were global this await would time out.
        var independent = await cache.GetOrCreateAsync(
            keys.Second,
            _ => Task.FromResult("second"),
            cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);

        independent.Should().Be("second");

        releaseFirst.SetResult();
        (await blocked.WaitAsync(Timeout, TestContext.Current.CancellationToken)).Should().Be("first");
    }

    /// <summary>
    /// Finds two keys that map to DIFFERENT stripes. The mapping is the one
    /// <see cref="KeyedSemaphoreStripe"/> applies, and string hashing is randomized per process, so
    /// the pair has to be chosen at run time: two hard-coded keys would collide on some runs and
    /// deadlock the concurrency test.
    /// </summary>
    /// <returns>Two keys guaranteed to sit on different stripes.</returns>
    private static (string First, string Second) DistinctStripeKeys()
    {
        var first = "parallel-0";
        for (var i = 1; i < 1000; i++)
        {
            var candidate = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"parallel-{i}");
            if (Stripe(candidate) != Stripe(first))
                return (first, candidate);
        }

        throw new InvalidOperationException("No two of the candidate keys landed on different stripes.");

        static uint Stripe(string key) =>
            (uint)string.GetHashCode(key, StringComparison.Ordinal) % KeyedSemaphoreStripe.DefaultWidth;
    }

    /// <summary>
    /// Minimal in-memory <see cref="ICacheService"/> that inherits every default member and records
    /// what was written.
    /// </summary>
    private sealed class RecordingCacheService : ICacheService
    {
        private readonly ConcurrentDictionary<string, object?> _store = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(string Key, TimeSpan? Expiration)> _sets = new();

        /// <summary>Gets every write, in order.</summary>
        public IReadOnlyList<(string Key, TimeSpan? Expiration)> Sets => [.. _sets];

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.TryGetValue(key, out var stored) && stored is T typed ? typed : default);

        public Task SetAsync<T>(
            string key,
            T value,
            TimeSpan? expiration = null,
            CancellationToken cancellationToken = default)
        {
            _store[key] = value;
            _sets.Enqueue((key, expiration));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                _store.TryRemove(key, out _);
            }

            return Task.CompletedTask;
        }
    }
}
