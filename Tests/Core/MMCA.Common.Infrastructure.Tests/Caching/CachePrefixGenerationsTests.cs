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
    public async Task SuffixAsync_AfterAFailedRead_KeepsAnsweringWithTheSameSuffix()
    {
        ReadsFail();
        var sut = Create(LocalDuration);

        var first = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);
        var second = await sut.SuffixAsync("catalog:1", TestContext.Current.CancellationToken);

        first.Should().NotBeEmpty("a boundary never seen falls back to a fresh token, never to the empty baseline an old L2 entry may sit under");
        second.Should().Be(first, "within the back-off the key stays stable, so an entry written during the outage is found again");
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
