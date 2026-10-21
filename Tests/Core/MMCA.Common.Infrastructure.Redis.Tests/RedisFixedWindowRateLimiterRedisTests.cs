using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.RateLimiting;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace MMCA.Common.Infrastructure.Redis.Tests;

/// <summary>
/// Runs the distributed rate limiter's Lua counter against a REAL Redis (L203 review, R5). The unit
/// tier only asserts the script text and what the limiter sends to a mocked database; whether the
/// script actually increments, sets a TTL on the creating increment, and returns a count the limiter
/// can compare is observable only on a server.
/// </summary>
public sealed class RedisFixedWindowRateLimiterRedisTests : IAsyncLifetime
{
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 20, 12, 0, 30, TimeSpan.Zero);

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    private ConnectionMultiplexer _multiplexer = null!;

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
            await _multiplexer.DisposeAsync();

        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_AgainstRealRedis_CountsTheWindowAndGivesTheKeyABoundedTtl()
    {
        var partition = $"test:global:{Guid.NewGuid():N}";
        await using var sut = new RedisFixedWindowRateLimiter(
            _multiplexer,
            partition,
            permitLimit: 2,
            NullLogger.Instance,
            new FixedTimeProvider(PinnedNow));
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"rl:{partition}:{PinnedNow.ToUnixTimeSeconds() / 60}");

        using var first = await sut.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var second = await sut.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var third = await sut.AcquireAsync(1, TestContext.Current.CancellationToken);

        first.IsAcquired.Should().BeTrue();
        second.IsAcquired.Should().BeTrue();
        third.IsAcquired.Should().BeFalse("the window's allowance of 2 is spent");

        var database = _multiplexer.GetDatabase();
        ((long)await database.StringGetAsync(key)).Should().Be(3, "every acquisition increments the shared counter");
        var ttl = await database.KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull("the creating increment must set an expiry, or the key lives forever");
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(65));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
