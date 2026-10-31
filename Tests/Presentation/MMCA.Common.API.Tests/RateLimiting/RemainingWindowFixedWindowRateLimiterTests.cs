using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.RateLimiting;

namespace MMCA.Common.API.Tests.RateLimiting;

/// <summary>
/// Unit tests for the in-memory fixed window's retry hint. The framework limiter answered every
/// rejection with the whole window (Retry-After: 60 however much of the minute was left, Store local
/// test run 2, X-18); the wrapper must report the time actually remaining.
/// </summary>
public sealed class RemainingWindowFixedWindowRateLimiterTests
{
    private static RemainingWindowFixedWindowRateLimiter CreateLimiter(TimeProvider timeProvider, int permitLimit = 1) =>
        new(
            new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = permitLimit,
                QueueLimit = 0,
            },
            timeProvider);

    [Fact]
    public void Rejection_CarriesTheTimeLeftInTheWindow_NotTheWindowLength()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateLimiter(time);
        using var first = sut.AttemptAcquire();
        time.Advance(TimeSpan.FromSeconds(35));

        using var rejected = sut.AttemptAcquire();

        first.IsAcquired.Should().BeTrue();
        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task AsyncRejection_CarriesTheTimeLeftInTheWindow()
    {
        var time = new FakeTimeProvider();
        await using var sut = CreateLimiter(time);
        using var first = await sut.AcquireAsync();
        time.Advance(TimeSpan.FromSeconds(10));

        using var rejected = await sut.AcquireAsync();

        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public void TryReplenish_OnceTheWindowHasPassed_StartsANewWindowForTheHint()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateLimiter(time);
        time.Advance(TimeSpan.FromSeconds(70));

        sut.TryReplenish();
        time.Advance(TimeSpan.FromSeconds(5));

        sut.TimeUntilNextWindow().Should().Be(TimeSpan.FromSeconds(55));
    }

    [Fact]
    public void TryReplenish_InsideTheWindow_KeepsTheWindowStart()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateLimiter(time);
        time.Advance(TimeSpan.FromSeconds(20));

        sut.TryReplenish();

        sut.TimeUntilNextWindow().Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void ARequestLargerThanTheAllowance_IsRefusedWithoutRewriting()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateLimiter(time, permitLimit: 2);

        var act = () => sut.AttemptAcquire(3);

        act.Should().Throw<ArgumentOutOfRangeException>("the framework limiter refuses a permit count above its limit");
    }

    [Fact]
    public void ThePartitionedLimiter_DrivesReplenishment_ThroughTheWrapper()
    {
        using var sut = CreateLimiter(TimeProvider.System);

        sut.IsAutoReplenishing.Should().BeFalse("the owning PartitionedRateLimiter calls TryReplenish on its own timer");
        sut.ReplenishmentPeriod.Should().Be(TimeSpan.FromMinutes(1));
    }
}
