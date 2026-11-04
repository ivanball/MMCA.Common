using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Caching;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Tests.Caching;

/// <summary>
/// The Redis-outage posture of the generation tokens behind prefix invalidation. A failed token
/// read must not turn every cache call into a Redis round trip that waits out the timeout (that
/// would switch caching off for the outage's length), and must not lose the token this process
/// already knew, or every L1 entry it addresses would stop being found.
/// </summary>
public sealed class CachePrefixGenerationsTests
{
    private static readonly TimeSpan LocalDuration = TimeSpan.FromSeconds(30);

    private readonly Mock<IDatabase> _database = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private int _reads;

    public CachePrefixGenerationsTests() =>
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(_database.Object);

    [Fact]
    public async Task SuffixAsync_AfterAFailedRead_DoesNotCallRedisAgainWithinTheBackOff()
    {
        ReadsFail();
        var sut = Create(LocalDuration);

        await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);
        var readsAfterFirstCall = _reads;

        await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);
        await sut.SuffixAsync("catalog:2", TestContext.Current.CancellationToken);

        readsAfterFirstCall.Should().Be(2, "the root and 'catalog:' boundaries are each read once");
        _reads.Should().Be(readsAfterFirstCall, "an outage costs one failed round trip per boundary per back-off window, not one per call");
    }

    [Fact]
    public async Task SuffixAsync_AfterAFailedReadOfANeverSeenBoundary_AnswersWithTheSharedBaseline()
    {
        ReadsFail();
        var sut = Create(LocalDuration);

        var first = await sut.SuffixAsync("login:fail:someone", TestContext.Current.CancellationToken);
        var second = await sut.SuffixAsync("login:fail:someone", TestContext.Current.CancellationToken);

        first.Should().BeEmpty(
            "a never-seen boundary falls back to the baseline every replica shares, so lockout flags and counters stay shared on a cold replica instead of failing open in a private keyspace");
        second.Should().Be(first, "within the back-off the key stays stable");
    }

    [Fact]
    public async Task SuffixAsync_WhenTheRedisClientCancelsTheRead_FallsBackInsteadOfThrowing()
    {
        // A cancellation raised inside the Redis client is not the caller's: it takes the failure
        // path, so the cache call stays fail-soft.
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromException<RedisValue>(new OperationCanceledException("client gave up")));
        var sut = Create(LocalDuration);

        var suffix = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);

        suffix.Should().BeEmpty();
    }

    [Fact]
    public async Task SuffixAsync_WhenTheCallerCancels_ThrowsTheCallersCancellation()
    {
        var gate = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(gate.Task);
        var sut = Create(LocalDuration);
        using var cts = new CancellationTokenSource();

        var call = sut.SuffixAsync("catalog:1", cts.Token).AsTask();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        gate.SetResult(RedisValue.Null);
    }

    [Fact]
    public async Task SuffixAsync_WithAnExpiredButRetainedToken_ServesItAtOnceAndRefreshesOnceInTheBackground()
    {
        var sut = Create(TimeSpan.FromMilliseconds(50));
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags _) =>
            {
                Interlocked.Increment(ref _reads);
                return Task.FromResult(key.ToString() == "hcg:catalog:" ? (RedisValue)"known" : RedisValue.Null);
            });
        (await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken)).Should().Be("|known");
        await Task.Delay(TimeSpan.FromMilliseconds(120), TestContext.Current.CancellationToken);

        // Redis now hangs: a caller that waited on it would hang too.
        var gate = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref _reads);
                return gate.Task;
            });
        var readsBefore = _reads;

        var first = sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);
        var second = sut.SuffixAsync("catalog:2", TestContext.Current.CancellationToken);

        first.IsCompleted.Should().BeTrue("an expired but retained token is served without waiting on Redis");
        second.IsCompleted.Should().BeTrue();
        (await first).Should().Be("|known");
        (_reads - readsBefore).Should().Be(2, "one background refresh per boundary ('' and 'catalog:'), shared by every caller");

        gate.SetResult("refreshed");
    }

    [Fact]
    public async Task SuffixAsync_AReadInFlightWhenThisProcessInvalidates_CannotPutTheOldTokenBack()
    {
        var gate = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(gate.Task);
        var sut = Create(LocalDuration);

        // The read is decided and in flight, then this process invalidates the prefix, then the read
        // answers with the value Redis held before the invalidation's write landed.
        var inFlight = sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);
        await sut.InvalidateAsync("catalog:", TestContext.Current.CancellationToken);
        gate.SetResult(RedisValue.Null);

        var answered = await inFlight;
        var afterwards = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);

        answered.Should().NotBeEmpty("the in-flight read resolves to the invalidating process's own token");
        afterwards.Should().Be(answered);
    }

    [Fact]
    public async Task SuffixAsync_WhenTheReadFailsAfterATokenWasKnown_UsesTheLastKnownToken()
    {
        // Zero local duration: every call re-reads, so the second call is guaranteed to hit Redis.
        var sut = Create(TimeSpan.Zero);
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags _) =>
            {
                Interlocked.Increment(ref _reads);
                return Task.FromResult(key.ToString() == "hcg:catalog:" ? (RedisValue)"known" : RedisValue.Null);
            });

        var healthy = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);

        ReadsFail();
        var duringOutage = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);

        healthy.Should().Be("|known");
        duringOutage.Should().Be("|known", "the L1 entry stored under the last known token stays addressed");
    }

    [Fact]
    public async Task InvalidateAsync_WhenTheRedisWriteFails_StillAdoptsTheNewTokenLocally()
    {
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        _database.SetReturnsDefault(Task.FromException<bool>(new InvalidOperationException("Redis unreachable")));
        var sut = Create(LocalDuration);

        var before = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);

        var invalidate = () => sut.InvalidateAsync("catalog:", TestContext.Current.CancellationToken);
        await invalidate.Should().ThrowAsync<InvalidOperationException>(
            "the failure surfaces to the caching command decorator, which logs it and keeps the command's success");

        var after = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);
        before.Should().BeEmpty();
        after.Should().NotBeEmpty("this process stops serving the prefix even though the write never reached Redis");
    }

    private CachePrefixGenerations Create(TimeSpan localDuration) =>
        new(_redis.Object, CacheKeyNamespace.None, localDuration, NullLogger.Instance);

    private void ReadsFail() =>
        _database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref _reads);
                return Task.FromException<RedisValue>(new TimeoutException("Redis timed out"));
            });
}
