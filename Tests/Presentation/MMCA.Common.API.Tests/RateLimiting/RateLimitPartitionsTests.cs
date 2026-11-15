using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.RateLimiting;

namespace MMCA.Common.API.Tests.RateLimiting;

/// <summary>
/// The public fixed-window partition a host builds its own policies from (Store manual test Run 4,
/// O6a: the checkout-quote limiter's 429 said <c>Retry-After: 60</c> however much of the minute was
/// left). It must hand out the remaining-window limiter, not the framework one.
/// </summary>
public sealed class RateLimitPartitionsTests
{
    [Fact]
    public void FixedWindowWithRemainingRetryAfter_KeepsTheKey_AndBuildsTheRemainingWindowLimiter()
    {
        var partition = RateLimitPartitions.FixedWindowWithRemainingRetryAfter("user-1", 5, TimeSpan.FromMinutes(1));

        partition.PartitionKey.Should().Be("user-1");
        using var limiter = partition.Factory(partition.PartitionKey);
        limiter.Should().BeOfType<RemainingWindowFixedWindowRateLimiter>();
    }

    [Fact]
    public void FixedWindowWithRemainingRetryAfter_RejectionCarriesTheTimeLeftInTheWindow()
    {
        var time = new FakeTimeProvider();
        var partition = RateLimitPartitions.FixedWindowWithRemainingRetryAfter("user-1", 2, TimeSpan.FromSeconds(30), time);
        using var limiter = partition.Factory(partition.PartitionKey);

        using var first = limiter.AttemptAcquire();
        using var second = limiter.AttemptAcquire();
        time.Advance(TimeSpan.FromSeconds(12));
        using var rejected = limiter.AttemptAcquire();

        first.IsAcquired.Should().BeTrue();
        second.IsAcquired.Should().BeTrue();
        rejected.IsAcquired.Should().BeFalse("the permit limit is honoured and nothing is queued");
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(18));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    public void FixedWindowWithRemainingRetryAfter_RejectsANonPositiveLimitOrWindow(int permitLimit, int windowSeconds)
    {
        var act = () => RateLimitPartitions.FixedWindowWithRemainingRetryAfter("user-1", permitLimit, TimeSpan.FromSeconds(windowSeconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
