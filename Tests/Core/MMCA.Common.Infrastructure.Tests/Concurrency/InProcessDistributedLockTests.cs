using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Concurrency;

namespace MMCA.Common.Infrastructure.Tests.Concurrency;

/// <summary>
/// Tests for the process-local <c>IDistributedLock</c> fallback used when no Redis connection is
/// registered. It must still honor the contract the Redis implementation does: exclusive per exact
/// key, bounded wait, owner-scoped and idempotent release.
/// </summary>
public sealed class InProcessDistributedLockTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly InProcessDistributedLock _sut =
        new(NullLogger<InProcessDistributedLock>.Instance);

    [Fact]
    public async Task TryAcquireAsync_WhenFree_Acquires()
    {
        IAsyncDisposable? handle = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        handle.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenAlreadyHeld_ReturnsNull()
    {
        await using IAsyncDisposable? held = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        IAsyncDisposable? second = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        held.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_AfterRelease_AcquiresAgain()
    {
        IAsyncDisposable? first = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);
        await first!.DisposeAsync();

        IAsyncDisposable? second = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        second.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_DoesNotBlockADifferentKey()
    {
        await using IAsyncDisposable? held = await _sut.TryAcquireAsync(
            "key-a", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        IAsyncDisposable? other = await _sut.TryAcquireAsync(
            "key-b", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        held.Should().NotBeNull();
        other.Should().NotBeNull(
            "keys are exact, not hashed into shared stripes: false sharing would report a key as held that nobody holds");
    }

    [Fact]
    public async Task TryAcquireAsync_WhenTheHolderReleasesDuringTheWait_Acquires()
    {
        IAsyncDisposable? held = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Task<IAsyncDisposable?> waiter = _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await held!.DisposeAsync();

        (await waiter).Should().NotBeNull();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndDoesNotReleaseTheNextHolder()
    {
        IAsyncDisposable? first = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);
        await first!.DisposeAsync();

        await using IAsyncDisposable? second = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);
        await first.DisposeAsync();

        second.Should().NotBeNull();
        IAsyncDisposable? third = await _sut.TryAcquireAsync(
            "key", Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);
        third.Should().BeNull("the second holder still owns the key after the first handle is disposed again");
    }
}
