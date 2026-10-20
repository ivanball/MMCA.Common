using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.RateLimiting;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.API.Tests.RateLimiting;

/// <summary>
/// Unit tests for the shared-counter rate limiter. The load-bearing decisions are the permit
/// comparison, the one-shot TTL on the key that opens a window, and the fail-open posture on a
/// Redis fault: a limiter that failed closed would turn a cache outage into a site-wide 429 storm.
/// </summary>
public sealed class RedisFixedWindowRateLimiterTests
{
    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task AcquireAsync_WhenCountIsInsideTheLimit_Permits()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync();

        lease.IsAcquired.Should().BeTrue();
        database.Verify(
            d => d.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.Is<RedisValue[]>(values => values[0] == 1L),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_AtExactlyTheLimit_StillPermits()
    {
        var (connection, _) = CreateConnection(incrementResult: 5);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync();

        lease.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_PastTheLimit_Rejects()
    {
        var (connection, _) = CreateConnection(incrementResult: 6);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync();

        lease.IsAcquired.Should().BeFalse();
    }

    // Only the increment that CREATES the window's key sets its TTL. Re-stamping the expiry on
    // every request would slide the window forward for a caller who keeps hitting it, so the key
    // would never expire and the allowance would never reset. The TTL now rides the increment's own
    // script (L203), so what is pinned here is the TTL argument and the script's create-only guard.
    [Fact]
    public async Task AcquireAsync_OnTheFirstRequestOfAWindow_SetsTheKeyExpiry()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync();

        database.Verify(
            d => d.ScriptEvaluateAsync(
                It.Is<string>(script => script.Contains("EXPIRE", StringComparison.Ordinal)),
                It.IsAny<RedisKey[]>(),
                It.Is<RedisValue[]>(values => values.Length == 2 && (int)values[1] > 60),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_OnALaterRequestOfTheSameWindow_DoesNotResetTheExpiry()
    {
        var (connection, database) = CreateConnection(incrementResult: 2);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync();

        database.Verify(
            d => d.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()),
            Times.Never);

        // The create-only decision now runs inside the script, on the server's own count.
        RedisFixedWindowRateLimiter.IncrementScript.Should().Contain(
            "if count <= tonumber(ARGV[1]) then",
            "a later request of the same window must not reset the expiry");
    }

    // L203: a separate EXPIRE round trip after INCR leaves a key with no TTL forever if the process
    // dies, the connection drops or the call times out between the two. The TTL must be set in the
    // same atomic step as the increment, and no second command may follow it.
    [Fact]
    public async Task AcquireAsync_SetsTheExpiryAtomicallyWithTheIncrement()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync();

        database.Verify(
            d => d.ScriptEvaluateAsync(
                It.Is<string>(script => script.Contains("INCRBY", StringComparison.Ordinal)
                    && script.Contains("EXPIRE", StringComparison.Ordinal)),
                It.Is<RedisKey[]>(keys => keys.Length == 1),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
        database.Invocations.Should().ContainSingle(
            "the increment and its TTL are one round trip; any second command reopens the window for a TTL-less key");
    }

    // The key must carry both the partition and the window, or two partitions would share an
    // allowance and a window rollover would never reset one.
    [Fact]
    public async Task AcquireAsync_KeysTheCounterByPartitionAndWindow()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        RedisKey capturedKey = default;
        database.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[]?, RedisValue[]?, CommandFlags>((_, keys, _, _) => capturedKey = keys![0])
            .ReturnsAsync(RedisResult.Create((RedisValue)1L));

        var timeProvider = new FakeTimeProvider(FixedInstant);
        await using var sut = new RedisFixedWindowRateLimiter(
            connection.Object, "global:alice", 5, NullLogger.Instance, timeProvider);

        using var lease = await sut.AcquireAsync();

        var expectedWindow = FixedInstant.ToUnixTimeSeconds() / 60;
        capturedKey.ToString().Should().Be(
            string.Create(CultureInfo.InvariantCulture, $"rl:global:alice:{expectedWindow}"));
    }

    // Rate limiting protects capacity; it must never become the reason a healthy request is
    // rejected, so a dead Redis permits rather than denies.
    [Fact]
    public async Task AcquireAsync_WhenRedisFaults_FailsOpen()
    {
        var database = new Mock<IDatabase>();
        database.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down", innerException: null, CommandStatus.Unknown));

        var connection = new Mock<IConnectionMultiplexer>();
        connection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(database.Object);

        await using var sut = CreateLimiter(connection.Object, permitLimit: 1);

        using var lease = await sut.AcquireAsync();

        lease.IsAcquired.Should().BeTrue();
    }

    // A request asking for more permits than the window can ever hold is rejected without a round
    // trip: incrementing for it would burn the whole window's allowance on a request that can
    // never be satisfied.
    [Fact]
    public async Task AcquireAsync_WhenPermitCountExceedsTheLimit_RejectsWithoutTouchingRedis()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync(permitCount: 6);

        lease.IsAcquired.Should().BeFalse();
        database.Invocations.Should().BeEmpty();
    }

    // The ASP.NET Core middleware tries AttemptAcquire FIRST and falls through to AcquireAsync only
    // when that lease is not acquired. A granting synchronous path therefore admitted every request
    // uncounted; it must decline without a Redis round trip so the shared counter decides.
    [Fact]
    public async Task AttemptAcquire_IsNotAcquiredAndNeverTouchesRedis()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = sut.AttemptAcquire();

        lease.IsAcquired.Should().BeFalse();
        database.Invocations.Should().BeEmpty();
    }

    // Reported rather than null so the owning PartitionedRateLimiter can evict partitions: keys
    // embed user identity, so a never-idle limiter would grow the partition table without bound.
    [Fact]
    public async Task IdleDuration_IsReportedSoUnusedPartitionsCanBeEvicted()
    {
        var (connection, _) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        sut.IdleDuration.Should().NotBeNull();
        sut.IdleDuration!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetStatistics_ReturnsNullBecauseTheCounterLivesInRedis()
    {
        var (connection, _) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        sut.GetStatistics().Should().BeNull();
    }

    [Fact]
    public void Constructor_RejectsANonPositivePermitLimit()
    {
        var (connection, _) = CreateConnection(incrementResult: 1);

        Action act = () => _ = new RedisFixedWindowRateLimiter(
            connection.Object, "global:alice", 0, NullLogger.Instance);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static RedisFixedWindowRateLimiter CreateLimiter(IConnectionMultiplexer connection, int permitLimit) =>
        new(connection, "global:alice", permitLimit, NullLogger.Instance, new FakeTimeProvider(FixedInstant));

    private static (Mock<IConnectionMultiplexer> Connection, Mock<IDatabase> Database) CreateConnection(long incrementResult)
    {
        var database = new Mock<IDatabase>();
        database.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)incrementResult));

        var connection = new Mock<IConnectionMultiplexer>();
        connection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(database.Object);

        return (connection, database);
    }
}
