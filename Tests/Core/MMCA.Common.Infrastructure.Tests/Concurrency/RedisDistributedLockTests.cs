using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Concurrency;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Tests.Concurrency;

/// <summary>
/// Tests for the Redis <c>SET NX PX</c> lock behind <c>IDistributedLock</c>: acquisition must be a
/// single atomic conditional set carrying a TTL (so a crashed holder cannot wedge the key), and
/// release must be a compare-and-delete on the owner token (so a holder whose TTL already lapsed
/// cannot free the lock a different replica now owns).
/// </summary>
public sealed class RedisDistributedLockTests
{
    private const string Key = "idempotency:abc";
    private const string QualifiedKey = "lock:idempotency:abc";

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly Mock<IDatabase> _database = new();
    private readonly Mock<IConnectionMultiplexer> _multiplexer = new();
    private readonly RedisDistributedLock _sut;

    public RedisDistributedLockTests()
    {
        _multiplexer
            .Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()))
            .Returns(_database.Object);
        _database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)1));

        _sut = new RedisDistributedLock(_multiplexer.Object, NullLogger<RedisDistributedLock>.Instance);
    }

    private void SetupAcquire(params bool[] results)
    {
        var attempt = 0;
        _database
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(() =>
            {
                var index = Interlocked.Increment(ref attempt) - 1;
                return results[Math.Min(index, results.Length - 1)];
            });
    }

    [Fact]
    public async Task TryAcquireAsync_WhenKeyIsFree_SetsOwnerTokenConditionallyWithTheTtl()
    {
        SetupAcquire(true);

        IAsyncDisposable? handle = await _sut.TryAcquireAsync(
            Key, Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        handle.Should().NotBeNull();
        _database.Verify(
            x => x.StringSetAsync(
                (RedisKey)QualifiedKey,
                It.Is<RedisValue>(v => !v.IsNullOrEmpty),
                Ttl,
                false,
                When.NotExists,
                It.IsAny<CommandFlags>()),
            Times.Once,
            "acquisition must be one conditional SET carrying the TTL, never a GET-then-SET");
    }

    [Fact]
    public async Task TryAcquireAsync_WhenHeldElsewhere_ReturnsNullWithoutExecuting()
    {
        SetupAcquire(false);

        IAsyncDisposable? handle = await _sut.TryAcquireAsync(
            Key, Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        handle.Should().BeNull("a zero wait is a single non-blocking attempt");
        _database.Verify(
            x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenHolderReleasesDuringTheWait_Acquires()
    {
        SetupAcquire(false, false, true);

        IAsyncDisposable? handle = await _sut.TryAcquireAsync(
            Key, Ttl, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        handle.Should().NotBeNull("the wait retries until the holder releases");
    }

    [Fact]
    public async Task DisposeAsync_DeletesOnlyWhenTheStoredValueIsStillItsOwnToken()
    {
        SetupAcquire(true);
        RedisValue writtenToken = default;
        _database
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>(
                (_, value, _, _, _, _) => writtenToken = value)
            .ReturnsAsync(true);

        IAsyncDisposable? handle = await _sut.TryAcquireAsync(
            Key, Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);
        await handle!.DisposeAsync();

        _database.Verify(
            x => x.ScriptEvaluateAsync(
                It.Is<string>(script => script.Contains("del", StringComparison.OrdinalIgnoreCase)),
                It.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == (RedisKey)QualifiedKey),
                It.Is<RedisValue[]>(values => values.Length == 1 && values[0] == writtenToken),
                It.IsAny<CommandFlags>()),
            Times.Once,
            "release is a compare-and-delete on the owner token, not an unconditional DEL");
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        SetupAcquire(true);

        IAsyncDisposable? handle = await _sut.TryAcquireAsync(
            Key, Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);
        await handle!.DisposeAsync();
        await handle.DisposeAsync();

        _database.Verify(
            x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenTheLockAlreadyExpired_DoesNotThrow()
    {
        SetupAcquire(true);
        _database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)0));

        IAsyncDisposable? handle = await _sut.TryAcquireAsync(
            Key, Ttl, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Func<Task> act = async () => await handle!.DisposeAsync();

        await act.Should().NotThrowAsync("a lapsed TTL is logged, not surfaced to the guarded work");
    }
}
