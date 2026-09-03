using System.Diagnostics;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Outbox;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox;

/// <summary>
/// The signal is capped at one pending permit on purpose: <c>OutboxProcessor</c> drains every
/// pending message in one batch, so a burst of N saves carries exactly one wake-up's worth of
/// information. An uncapped semaphore let permits accumulate one per save, and each surplus permit
/// cost the processor a candidate-fetch round trip per data source that returned nothing.
/// </summary>
public sealed class OutboxSignalTests
{
    private static readonly TimeSpan NoWait = TimeSpan.Zero;

    [Fact]
    public async Task ManySignals_GrantExactlyOneWakeUp()
    {
        using var signal = new OutboxSignal();

        for (var i = 0; i < 50; i++)
        {
            signal.Signal();
        }

        // The first wait consumes the single permit.
        await signal.WaitAsync(NoWait, TestContext.Current.CancellationToken);

        // A second wait must find nothing pending and fall through on its timeout rather than
        // returning immediately 49 more times.
        var start = Stopwatch.GetTimestamp();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);

        Stopwatch.GetElapsedTime(start).Should().BeGreaterThan(TimeSpan.FromMilliseconds(50),
            "the surplus signals must have been absorbed, leaving the second wait to time out");
    }

    [Fact]
    public async Task SignalAfterDrain_GrantsAnotherWakeUp()
    {
        using var signal = new OutboxSignal();

        signal.Signal();
        await signal.WaitAsync(NoWait, TestContext.Current.CancellationToken);

        // Capping must not swallow a genuinely new signal raised after the batch drained.
        signal.Signal();
        var start = Stopwatch.GetTimestamp();
        await signal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromSeconds(1),
            "a signal raised after the drain must wake the processor immediately");
    }

    [Fact]
    public void ConcurrentSignals_DoNotThrow()
    {
        using var signal = new OutboxSignal();

        // Release() past the cap throws SemaphoreFullException; Signal() swallows it by contract.
        var act = () => Parallel.For(0, 200, _ => signal.Signal());

        act.Should().NotThrow();
    }
}
