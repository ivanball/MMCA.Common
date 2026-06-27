using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Aspire.Warmup;

namespace MMCA.Common.Aspire.Tests.Warmup;

/// <summary>
/// The warm-up hosted service (rubric §29): runs every <see cref="IWarmupTask"/> once at startup and
/// opens the readiness gate afterwards — and opens it even when a task fails, so a transient
/// dependency outage cannot wedge the replica permanently out of rotation (ADR-025).
/// </summary>
public sealed class WarmupHostedServiceTests
{
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
}
