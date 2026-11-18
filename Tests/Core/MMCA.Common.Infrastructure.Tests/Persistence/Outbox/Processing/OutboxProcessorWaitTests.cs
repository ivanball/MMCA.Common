using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Persistence.Polling;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox.Processing;

/// <summary>
/// Unit tests for <see cref="OutboxProcessor.ComputeWaitTime"/> — the smart-wait computation
/// deciding how long the processor sleeps between polling cycles.
/// </summary>
public sealed class OutboxProcessorWaitTests
{
    private static readonly DateTime Now = new(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(300);

    [Fact]
    public void NoPendingMessages_WaitsFullPollingInterval()
    {
        var wait = OutboxProcessor.ComputeWaitTime(null, Now, Delay, Interval);

        wait.Should().Be(Interval);
    }

    [Fact]
    public void PendingMessage_WaitsUntilJustAfterItBecomesEligible()
    {
        // Message occurred 2s ago, eligible after 5s -> due in 3s; the margin wakes the cycle just
        // after the strict cutoff has passed it.
        var wait = OutboxProcessor.ComputeWaitTime(Now.AddSeconds(-2), Now, Delay, Interval);

        wait.Should().Be(TimeSpan.FromSeconds(3) + PollingLoop.ReadinessMargin);
    }

    [Fact]
    public void MessageDueInUnderASecond_IsNotPushedToTheOneSecondFloor()
    {
        // Due in 70 ms: the floor used to turn this into a full second, so a wake that landed just
        // before eligibility delivered the event about a second late.
        var wait = OutboxProcessor.ComputeWaitTime(Now.AddMilliseconds(-4_930), Now, Delay, Interval);

        wait.Should().Be(TimeSpan.FromMilliseconds(70) + PollingLoop.ReadinessMargin);
    }

    [Fact]
    public void OverduePendingMessage_FlooredAtOneSecond()
    {
        // Became eligible between the query and the wait computation: never hot-loop.
        var wait = OutboxProcessor.ComputeWaitTime(Now.AddSeconds(-10), Now, Delay, Interval);

        wait.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MessageDueExactlyNow_FlooredAtOneSecond()
    {
        // Zero time left counts as overdue (the strict cutoff left it for a later cycle).
        var wait = OutboxProcessor.ComputeWaitTime(Now.AddSeconds(-5), Now, Delay, Interval);

        wait.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void WaitNeverExceedsPollingInterval()
    {
        // A long processing delay with a short interval: capped at the interval.
        var wait = OutboxProcessor.ComputeWaitTime(Now, Now, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        wait.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FutureDatedMessage_CappedAtPollingInterval()
    {
        // Clock skew: a future OccurredOn must not extend the wait beyond the interval.
        var wait = OutboxProcessor.ComputeWaitTime(Now.AddHours(1), Now, Delay, Interval);

        wait.Should().Be(Interval);
    }
}
