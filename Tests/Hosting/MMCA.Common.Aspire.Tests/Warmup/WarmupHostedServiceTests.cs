using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Aspire.Warmup;

namespace MMCA.Common.Aspire.Tests.Warmup;

/// <summary>
/// The warm-up hosted service (rubric §29): runs every <see cref="IWarmupTask"/> once at startup and
/// opens the readiness gate afterwards — and opens it even when a task fails, so a transient
/// dependency outage cannot wedge the replica permanently out of rotation (ADR-025). A task that
/// hangs instead of failing is bounded by a per-task timeout and lands on that same path.
/// </summary>
public sealed class WarmupHostedServiceTests
{
    /// <summary>
    /// Stand-in for the production 120s ceiling. The tasks under test either complete synchronously
    /// or never complete at all, so this value only decides how long the timeout case takes to run.
    /// </summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);

    private sealed class RecordingTask(string name) : IWarmupTask
    {
        public string Name { get; } = name;

        public bool Executed { get; private set; }

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Executed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingTask : IWarmupTask
    {
        public string Name => "Throwing";

        public Task ExecuteAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>A task that never completes and never faults: the case the timeout exists for.</summary>
    private sealed class HangingTask : IWarmupTask
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "Hanging";

        public bool Executed { get; private set; }

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Executed = true;
            return _never.Task;
        }
    }

    private static async Task RunAsync(WarmupReadinessGate gate, params IWarmupTask[] tasks)
    {
        using var service = new WarmupHostedService(tasks, gate, NullLogger<WarmupHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunsEveryTask_AndOpensTheGate()
    {
        var gate = new WarmupReadinessGate();
        var taskA = new RecordingTask("A");
        var taskB = new RecordingTask("B");

        await RunAsync(gate, taskA, taskB);

        taskA.Executed.Should().BeTrue();
        taskB.Executed.Should().BeTrue();
        gate.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task OpensTheGate_EvenWhenATaskThrows()
    {
        var gate = new WarmupReadinessGate();
        var survivor = new RecordingTask("survivor");

        await RunAsync(gate, new ThrowingTask(), survivor);

        gate.IsReady.Should().BeTrue("a failing warm-up task must not keep the replica out of rotation");
        survivor.Executed.Should().BeTrue("other tasks still run when one fails");
    }

    [Fact]
    public async Task OpensTheGate_WhenThereAreNoTasks()
    {
        var gate = new WarmupReadinessGate();

        await RunAsync(gate);

        gate.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task OpensTheGate_WhenATaskHangsPastItsTimeout()
    {
        var gate = new WarmupReadinessGate();
        var hanging = new HangingTask();
        var survivor = new RecordingTask("survivor");

        using var service = new WarmupHostedService(
            [hanging, survivor],
            gate,
            NullLogger<WarmupHostedService>.Instance,
            taskTimeout: ShortTimeout);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        hanging.Executed.Should().BeTrue();
        gate.IsReady.Should().BeTrue(
            "a task that hangs must not keep the replica out of rotation any more than one that fails");
        survivor.Executed.Should().BeTrue("other tasks still run alongside the hung one");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DoesNotWaitForTheTimeout_WhenEveryTaskIsFast()
    {
        var gate = new WarmupReadinessGate();
        var taskA = new RecordingTask("A");
        var taskB = new RecordingTask("B");

        using var service = new WarmupHostedService(
            [taskA, taskB],
            gate,
            NullLogger<WarmupHostedService>.Instance,
            taskTimeout: TimeSpan.FromMinutes(10));
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        gate.IsReady.Should().BeTrue("the timeout is a ceiling, not a delay: completed tasks open the gate at once");
        taskA.Executed.Should().BeTrue();
        taskB.Executed.Should().BeTrue();

        await service.StopAsync(CancellationToken.None);
    }
}
