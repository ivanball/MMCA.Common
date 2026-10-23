using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using MMCA.Common.Shared.Resilience;

namespace MMCA.Common.Shared.Tests.Resilience;

/// <summary>
/// Both halves of the 429 <c>Retry-After</c> contract: the server formats a lease's hint in whole
/// seconds rounded up, and the client reads either header form back and refuses to wait out one
/// longer than <see cref="HttpResilienceDefaults.MaxRetryAfterWait"/>.
/// </summary>
public sealed class RetryAfterHeaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MaxRetryAfterWait_IsTheLongestUiBackoffStep() =>
        HttpResilienceDefaults.MaxRetryAfterWait.Should().Be(TimeSpan.FromSeconds(8));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(1_001, "2")]
    [InlineData(3_999, "4")]
    [InlineData(4_000, "4")]
    [InlineData(59_750, "60")]
    [InlineData(-500, "0")]
    public void Format_RoundsUpToWholeSecondsAndNeverGoesNegative(int milliseconds, string expected) =>
        RetryAfterHeader.Format(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);

    [Fact]
    public void TryGetDelay_ReadsTheDeltaForm()
    {
        using var response = TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)));

        RetryAfterHeader.TryGetDelay(response, new FixedClock(Now), out var delay).Should().BeTrue();
        delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TryGetDelay_MeasuresTheDateFormAgainstTheClock()
    {
        using var response = TooManyRequests(new RetryConditionHeaderValue(Now.AddSeconds(12)));

        RetryAfterHeader.TryGetDelay(response, new FixedClock(Now), out var delay).Should().BeTrue();
        delay.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void TryGetDelay_ADateAlreadyPast_IsAZeroDelay()
    {
        using var response = TooManyRequests(new RetryConditionHeaderValue(Now.AddSeconds(-30)));

        RetryAfterHeader.TryGetDelay(response, new FixedClock(Now), out var delay).Should().BeTrue();
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryGetDelay_WithoutTheHeader_ReportsNothing()
    {
        using var response = TooManyRequests(retryAfter: null);

        RetryAfterHeader.TryGetDelay(response, new FixedClock(Now), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(60, true)]
    [InlineData(8, false)]
    [InlineData(2, false)]
    public void ExceedsRetryBudget_ComparesA429AgainstTheBudget(int seconds, bool exceeds)
    {
        using var response = TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)));

        RetryAfterHeader.ExceedsRetryBudget(response, new FixedClock(Now)).Should().Be(exceeds);
    }

    [Fact]
    public void ExceedsRetryBudget_A429WithoutTheHeader_IsWithinBudget()
    {
        using var response = TooManyRequests(retryAfter: null);

        RetryAfterHeader.ExceedsRetryBudget(response, new FixedClock(Now)).Should().BeFalse(
            "a 429 that names no delay keeps the ordinary backoff");
    }

    [Fact]
    public void ExceedsRetryBudget_OnlyAppliesToA429()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));

        RetryAfterHeader.ExceedsRetryBudget(response, new FixedClock(Now)).Should().BeFalse();
        RetryAfterHeader.ExceedsRetryBudget(null, new FixedClock(Now)).Should().BeFalse(
            "an exception outcome has no response to read");
    }

    private static HttpResponseMessage TooManyRequests(RetryConditionHeaderValue? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
