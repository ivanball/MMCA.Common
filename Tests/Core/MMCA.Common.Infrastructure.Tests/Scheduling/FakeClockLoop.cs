using Microsoft.Extensions.Time.Testing;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// Drives an interval loop that waits on a <see cref="FakeTimeProvider"/> (every
/// <c>PeriodicBackgroundService</c> subclass) until the test observes the effect it is waiting for.
/// </summary>
internal static class FakeClockLoop
{
    /// <summary>
    /// Advances the fake clock one <paramref name="step"/> at a time, yielding a few real milliseconds
    /// after each advance so the awoken cycle can run, until <paramref name="observed"/> completes.
    /// <see cref="FakeTimeProvider.Advance"/> only completes a Delay whose timer already exists, so the
    /// loop tolerates the startup race between <c>StartAsync</c> and the first Delay registration. The
    /// iteration cap both fails a broken loop fast and keeps cumulative fake time bounded, so rows a
    /// test seeds as "inside the retention window" stay inside it.
    /// </summary>
    /// <param name="timeProvider">The fake clock the service under test waits on.</param>
    /// <param name="step">How far to advance per iteration (normally the service's interval).</param>
    /// <param name="observed">Completes when the test has seen the cycle it is waiting for.</param>
    /// <returns>A task that completes once <paramref name="observed"/> has, or faults on timeout.</returns>
    internal static async Task AdvanceUntilAsync(FakeTimeProvider timeProvider, TimeSpan step, Task observed)
    {
        for (var i = 0; i < 100 && !observed.IsCompleted; i++)
        {
            timeProvider.Advance(step);

            // A REAL (system-clock) yield so the awoken cycle can run; the fake provider must not be
            // used here or the wait itself would need advancing.
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, CancellationToken.None);
        }

        await observed.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
    }
}
