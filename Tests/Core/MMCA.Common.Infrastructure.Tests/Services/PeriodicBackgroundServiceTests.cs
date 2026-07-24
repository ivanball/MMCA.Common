using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Coverage for <see cref="PeriodicBackgroundService"/>: the enablement gate, the startup
/// delay, interval-driven cycles, and the failing-cycle-never-kills-the-loop contract, all
/// driven deterministically through the <see cref="FakeTimeProvider"/> clock using the same
/// advance-until idiom as <c>OutboxCleanupServiceTests</c> (Advance only completes a Delay
/// whose timer already exists, so a single Advance can race the loop's registration).
/// </summary>
public sealed class PeriodicBackgroundServiceTests
{
    private static readonly TimeSpan SweepStartup = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task ExecuteAsync_RunsCyclesOnTheIntervalClock_AndOnlyWhenTimeAdvances()
    {
        var time = new FakeTimeProvider();
        using var sut = new CountingSweep(time);

        await sut.StartAsync(CancellationToken.None);

        (await AdvanceUntilCyclesAsync(time, sut, expected: 1)).Should().BeGreaterThanOrEqualTo(1,
            because: "advancing past the startup delay must run the first cycle");

        // With the clock frozen, no further cycles may sneak in: the loop is clock-driven.
        var frozen = sut.Cycles;
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, CancellationToken.None);
        sut.Cycles.Should().Be(frozen, because: "cycles only run when the interval elapses");

        (await AdvanceUntilCyclesAsync(time, sut, expected: frozen + 1)).Should().BeGreaterThanOrEqualTo(frozen + 1,
            because: "each elapsed interval produces a further cycle");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_CycleFailure_DoesNotStopTheLoop()
    {
        var time = new FakeTimeProvider();
        using var sut = new CountingSweep(time) { FailOnCycle = 1 };

        await sut.StartAsync(CancellationToken.None);

        (await AdvanceUntilCyclesAsync(time, sut, expected: 2)).Should().BeGreaterThanOrEqualTo(2,
            because: "the throwing first cycle is logged and the loop keeps sweeping");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_NeverRunsACycle()
    {
        var time = new FakeTimeProvider();
        using var sut = new CountingSweep(time) { Enabled = false };

        await sut.StartAsync(CancellationToken.None);
        sut.ExecuteTask.Should().NotBeNull();
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);

        time.Advance(SweepStartup + SweepInterval + SweepInterval);
        sut.Cycles.Should().Be(0, because: "the enablement gate exits before the loop starts");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_DuringStartupDelay_ExitsCleanly()
    {
        var time = new FakeTimeProvider();
        using var sut = new CountingSweep(time);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        sut.Cycles.Should().Be(0);
    }

    /// <summary>
    /// Advances the fake clock one interval at a time, yielding a few real milliseconds after
    /// each step so the loop's continuations run, until the cycle count reaches
    /// <paramref name="expected"/> or a bounded deadline passes (mirrors
    /// <c>OutboxCleanupServiceTests.AdvanceUntilSweepAsync</c>).
    /// </summary>
    private static async Task<int> AdvanceUntilCyclesAsync(FakeTimeProvider time, CountingSweep sut, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (sut.Cycles < expected && DateTime.UtcNow < deadline)
        {
            time.Advance(SweepInterval);
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, CancellationToken.None);
        }

        return sut.Cycles;
    }

    /// <summary>Test double counting cycles, optionally failing one of them.</summary>
    private sealed class CountingSweep(FakeTimeProvider time)
        : PeriodicBackgroundService(time, NullLogger<CountingSweep>.Instance)
    {
        private int _cycles;

        public bool Enabled { get; init; } = true;

        /// <summary>1-based index of a cycle that should throw; 0 = never.</summary>
        public int FailOnCycle { get; init; }

        public int Cycles => Volatile.Read(ref _cycles);

        protected override TimeSpan Interval => SweepInterval;

        protected override TimeSpan StartupDelay => SweepStartup;

        protected override bool IsEnabled => Enabled;

        protected override Task ExecuteCycleAsync(CancellationToken stoppingToken)
        {
            var cycle = Interlocked.Increment(ref _cycles);
            return cycle == FailOnCycle
                ? Task.FromException(new InvalidOperationException("simulated cycle failure"))
                : Task.CompletedTask;
        }
    }
}
