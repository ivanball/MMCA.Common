using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Processing;

/// <summary>
/// Tests for the lane wake on <see cref="InternalCommandSignal"/> (design-webhook-lane.md L1.2, L1.5):
/// the lane wake exists only once a lane waiter asked for it, so a host without the lane signals
/// exactly as before, and with one, a single <see cref="InternalCommandSignal.Signal"/> wakes the
/// shared loop and the lane, each through its own single-permit wake.
/// </summary>
public sealed class InternalCommandSignalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signal_WithNoLaneWaiter_CreatesNoLaneWake()
    {
        using var signal = new InternalCommandSignal();

        signal.Signal();

        signal.HasLaneWake.Should().BeFalse("only a lane waiter creates the lane wake (L1.2: Signal touches only the shared wake)");
        await signal.WaitAsync(TimeSpan.FromMinutes(5), Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        signal.HasLaneWake.Should().BeFalse("the shared waiter never creates it either");
    }

    [Fact]
    public async Task Signal_WakesTheSharedWaiterAndTheLaneWaiter()
    {
        using var signal = new InternalCommandSignal();
        var lane = signal.WaitForLaneAsync(TimeSpan.FromMinutes(5), Ct);
        var shared = signal.WaitAsync(TimeSpan.FromMinutes(5), Ct);

        signal.HasLaneWake.Should().BeTrue("the first lane wait creates the lane wake");
        lane.IsCompleted.Should().BeFalse("no signal yet");
        shared.IsCompleted.Should().BeFalse("no signal yet");

        signal.Signal();

        await Task.WhenAll(lane, shared).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        lane.IsCompletedSuccessfully.Should().BeTrue("one Signal wakes the lane through its own wake");
        shared.IsCompletedSuccessfully.Should().BeTrue("and the shared loop through the shared wake");
    }
}
