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
            d => d.StringIncrementAsync(It.IsAny<RedisKey>(), 1L, It.IsAny<CommandFlags>()),
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
    // would never expire and the allowance would never reset.
    [Fact]
    public async Task AcquireAsync_OnTheFirstRequestOfAWindow_SetsTheKeyExpiry()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 5);

        using var lease = await sut.AcquireAsync();

        database.Verify(
            d => d.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.Is<TimeSpan?>(expiry => expiry > TimeSpan.FromSeconds(60)),
                It.IsAny<ExpireWhen>(),
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
    }

    // The key must carry both the partition and the window, or two partitions would share an
    // allowance and a window rollover would never reset one.
    [Fact]
    public async Task AcquireAsync_KeysTheCounterByPartitionAndWindow()
    {
        var (connection, database) = CreateConnection(incrementResult: 1);
        RedisKey capturedKey = default;
        database.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, long, CommandFlags>((key, _, _) => capturedKey = key)
            .ReturnsAsync(1L);

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
        database.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

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
        database.Verify(
            d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    // The synchronous path exists only to satisfy the base contract: the ASP.NET Core middleware
    // uses AcquireAsync, and blocking a request thread on a Redis round trip would be worse than
    // the fail-open posture the limiter already takes.
    [Fact]
    public async Task AttemptAcquire_AlwaysPermits()
    {
        var (connection, database) = CreateConnection(incrementResult: 99);
        await using var sut = CreateLimiter(connection.Object, permitLimit: 1);

        using var lease = sut.AttemptAcquire();

        lease.IsAcquired.Should().BeTrue();
        database.Verify(
            d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()),
            Times.Never);
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
        database.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(incrementResult);
        database.Setup(d => d.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var connection = new Mock<IConnectionMultiplexer>();
        connection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(database.Object);

        return (connection, database);
    }
}
