using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using MMCA.Common.Infrastructure.Caching;

namespace MMCA.Common.Infrastructure.Tests.Caching;

public sealed class MemoryCacheServiceTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MemoryCacheService _sut;

    public MemoryCacheServiceTests() =>
        _sut = new MemoryCacheService(_cache);

    public void Dispose() => _cache.Dispose();

    // ── GetAsync ──
    [Fact]
    public async Task GetAsync_WhenKeyDoesNotExist_ReturnsDefault()
    {
        var result = await _sut.GetAsync<string>("missing-key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_AfterSet_ReturnsStoredValue()
    {
        await _sut.SetAsync("key1", "value1");
        var result = await _sut.GetAsync<string>("key1");
        result.Should().Be("value1");
    }

    [Fact]
    public async Task GetAsync_WithIntValue_ReturnsStoredValue()
    {
        await _sut.SetAsync("int-key", 42);
        var result = await _sut.GetAsync<int>("int-key");
        result.Should().Be(42);
    }

    [Fact]
    public async Task GetAsync_WhenKeyStoredUnderDifferentType_ReturnsCleanMissWithoutThrowing()
    {
        await _sut.SetAsync("reused-key", 42);

        // A key reused under a mismatched T must surface as a clean miss, not an InvalidCastException.
        var result = await FluentActions.Invoking(() => _sut.GetAsync<string>("reused-key"))
            .Should().NotThrowAsync();

        result.Subject.Should().BeNull();
    }

    // ── SetAsync ──
    [Fact]
    public async Task SetAsync_WithExpiration_StoresValue()
    {
        await _sut.SetAsync("exp-key", "data", TimeSpan.FromMinutes(5));
        var result = await _sut.GetAsync<string>("exp-key");
        result.Should().Be("data");
    }

    [Fact]
    public async Task SetAsync_WithoutExpiration_StoresValue()
    {
        await _sut.SetAsync("no-exp", "data");
        var result = await _sut.GetAsync<string>("no-exp");
        result.Should().Be("data");
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingKey()
    {
        await _sut.SetAsync("key", "original");
        await _sut.SetAsync("key", "updated");
        var result = await _sut.GetAsync<string>("key");
        result.Should().Be("updated");
    }

    // ── RemoveAsync ──
    [Fact]
    public async Task RemoveAsync_RemovesKey()
    {
        await _sut.SetAsync("to-remove", "val");
        await _sut.RemoveAsync("to-remove");
        var result = await _sut.GetAsync<string>("to-remove");
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_NonExistentKey_DoesNotThrow() =>
        await FluentActions.Invoking(() => _sut.RemoveAsync("nonexistent"))
            .Should().NotThrowAsync();

    // ── RemoveByPrefixAsync ──
    [Fact]
    public async Task RemoveByPrefixAsync_RemovesMatchingKeys()
    {
        await _sut.SetAsync("product:1", "A");
        await _sut.SetAsync("product:2", "B");
        await _sut.SetAsync("category:1", "C");

        await _sut.RemoveByPrefixAsync("product:");

        (await _sut.GetAsync<string>("product:1")).Should().BeNull();
        (await _sut.GetAsync<string>("product:2")).Should().BeNull();
        (await _sut.GetAsync<string>("category:1")).Should().Be("C");
    }

    [Fact]
    public async Task RemoveByPrefixAsync_NoMatchingKeys_DoesNotThrow()
    {
        await _sut.SetAsync("other:1", "val");
        await FluentActions.Invoking(() => _sut.RemoveByPrefixAsync("missing:"))
            .Should().NotThrowAsync();
    }

    // ── Overwriting a key must not lose its prefix-eviction tracking ──
    // IMemoryCache queues post-eviction callbacks to the thread pool, so overwriting a live key
    // fires the OLD entry's callback asynchronously. When that callback removed the key
    // unconditionally it could land after the replacement was tracked, deleting the record for an
    // entry that was still cached: live in the cache, invisible to RemoveByPrefixAsync, and
    // clearable only by its TTL.
    [Fact]
    public async Task RemoveByPrefixAsync_AfterOverwritingAKey_StillEvictsIt()
    {
        await _sut.SetAsync("product:1", "first");
        await _sut.SetAsync("product:1", "second");

        // Give any queued post-eviction callback from the overwrite time to run.
        await Task.Delay(100);

        await _sut.RemoveByPrefixAsync("product:");

        (await _sut.GetAsync<string>("product:1")).Should().BeNull(
            "the replacement entry must remain tracked for prefix eviction");
    }

    [Fact]
    public async Task RemoveByPrefixAsync_AfterRepeatedOverwrites_StillEvictsEveryKey()
    {
        foreach (var round in Enumerable.Range(0, 20))
        {
            await _sut.SetAsync("product:1", $"value-{round.ToString(CultureInfo.InvariantCulture)}");
            await _sut.SetAsync("product:2", $"value-{round.ToString(CultureInfo.InvariantCulture)}");
        }

        await Task.Delay(100);

        await _sut.RemoveByPrefixAsync("product:");

        (await _sut.GetAsync<string>("product:1")).Should().BeNull();
        (await _sut.GetAsync<string>("product:2")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveByPrefixAsync_AfterExpiry_DoesNotResurrectTracking()
    {
        // The callback must still clean up on a genuine eviction; only Replaced is exempt.
        await _sut.SetAsync("product:1", "value", TimeSpan.FromMilliseconds(1));
        await Task.Delay(150);

        // Touch the cache so MemoryCache processes the expiry.
        (await _sut.GetAsync<string>("product:1")).Should().BeNull();

        await FluentActions.Invoking(() => _sut.RemoveByPrefixAsync("product:"))
            .Should().NotThrowAsync();
    }
}
