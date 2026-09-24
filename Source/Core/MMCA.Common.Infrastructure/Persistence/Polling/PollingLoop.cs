using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Persistence.Polling;

/// <summary>
/// The polling core shared by the table-draining background services (<c>OutboxProcessor</c> and
/// <c>InternalCommandProcessor</c>): the main loop with its smart wait, the per-source drain that
/// isolates one unreachable database from the rest, and the retry backoff with jitter. Each processor
/// keeps its own queries, logging, metrics and activities and passes them in, so the loop mechanics
/// live in exactly one place while every observable name stays with the processor that owns it.
/// </summary>
internal static class PollingLoop
{
    /// <summary>
    /// Brief startup delay so the host finishes initializing (module registration, migration) before
    /// the first cycle polls a queue table.
    /// </summary>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>Floor for the computed wait so an overdue row cannot hot-loop the processor.</summary>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs the processor loop: waits <see cref="StartupDelay"/>, returns after
    /// <paramref name="onNoTargets"/> when the host owns no relational source, and otherwise runs cycles
    /// until <paramref name="stoppingToken"/> is cancelled. A cycle that reports more ready work re-polls
    /// immediately; any other cycle is followed by a wait on the wake-up signal, bounded by
    /// <see cref="ComputeWaitTime"/>. A failed cycle is reported and treated as one with no pending work.
    /// </summary>
    /// <param name="timeProvider">Clock for the startup delay and the smart-wait instant.</param>
    /// <param name="hasTargets">Whether this host owns any relational source; evaluated once, after the startup delay.</param>
    /// <param name="onNoTargets">Reports that the processor is idle because there is nothing to drain.</param>
    /// <param name="runCycle">Drains every source once and reports whether more ready work remains and the earliest upcoming instant.</param>
    /// <param name="onCycleError">Reports a cycle that threw.</param>
    /// <param name="processingDelaySeconds">The configured grace period, in seconds.</param>
    /// <param name="pollingIntervalSeconds">The configured fallback polling interval, in seconds.</param>
    /// <param name="waitForSignal">Waits for a wake-up signal or the given timeout, whichever comes first.</param>
    /// <param name="stoppingToken">Stops the loop.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    internal static async Task RunAsync(
        TimeProvider timeProvider,
        Func<bool> hasTargets,
        Action onNoTargets,
        Func<CancellationToken, Task<(bool HasMoreWork, DateTime? EarliestUpcoming)>> runCycle,
        Action<Exception> onCycleError,
        int processingDelaySeconds,
        int pollingIntervalSeconds,
        Func<TimeSpan, CancellationToken, Task> waitForSignal,
        CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, timeProvider, stoppingToken).ConfigureAwait(false);

        if (!hasTargets())
        {
            onNoTargets();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            (bool HasMoreWork, DateTime? EarliestUpcoming) cycle = default;
            try
            {
                cycle = await runCycle(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown: exit gracefully.
                break;
            }
            catch (Exception ex)
            {
                onCycleError(ex);
            }

            if (cycle.HasMoreWork)
            {
                // A full batch was drained with progress: more ready rows may be waiting.
                continue;
            }

            // Wait for a signal (new rows written), the moment the earliest upcoming row becomes
            // ready (smart wait), or the fallback polling interval, whichever comes first.
            var wait = ComputeWaitTime(
                cycle.EarliestUpcoming,
                timeProvider.GetUtcNow().UtcDateTime,
                TimeSpan.FromSeconds(processingDelaySeconds),
                TimeSpan.FromSeconds(pollingIntervalSeconds));
            await waitForSignal(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes how long to wait before the next cycle: until the earliest upcoming row becomes ready
    /// (<paramref name="earliestUpcoming"/> plus the processing delay), capped at the polling interval
    /// and floored at one second to avoid hot-looping. Failed-but-already-ready rows never shorten the
    /// wait: they retry on the next signal or interval, which throttles permanently failing rows.
    /// </summary>
    /// <param name="earliestUpcoming">The instant the oldest not-yet-ready row was written or scheduled for, or null.</param>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <param name="processingDelay">The configured grace period.</param>
    /// <param name="pollingInterval">The configured fallback polling interval.</param>
    /// <returns>The wait before the next cycle.</returns>
    internal static TimeSpan ComputeWaitTime(
        DateTime? earliestUpcoming,
        DateTime utcNow,
        TimeSpan processingDelay,
        TimeSpan pollingInterval)
    {
        if (earliestUpcoming is null)
        {
            return pollingInterval;
        }

        var untilReady = earliestUpcoming.Value + processingDelay - utcNow;
        if (untilReady < MinimumWait)
        {
            untilReady = MinimumWait;
        }

        return untilReady < pollingInterval ? untilReady : pollingInterval;
    }

    /// <summary>
    /// Drains every target once and aggregates the per-source results: any source with more ready work
    /// asks for an immediate re-poll, the earliest upcoming instant across all sources drives the smart
    /// wait, and the backlog is summed for the depth gauge. A source that throws is reported and
    /// contributes nothing, so one unreachable database cannot starve the others.
    /// </summary>
    /// <param name="targets">The units to drain, in order.</param>
    /// <param name="drainSource">Drains one unit and reports its result and observed backlog.</param>
    /// <param name="onSourceError">Reports a unit that threw, with the unit's display name.</param>
    /// <param name="cancellationToken">Cancels the cycle; a cancellation it caused is rethrown.</param>
    /// <returns>The aggregated result and the total backlog observed.</returns>
    internal static async Task<(bool HasMoreWork, DateTime? EarliestUpcoming, long PendingDepth)> DrainAllAsync(
        List<TenantDataSourceTarget> targets,
        Func<TenantDataSourceTarget, CancellationToken, Task<(bool HasMoreWork, DateTime? EarliestUpcoming, long PendingDepth)>> drainSource,
        Action<string, Exception> onSourceError,
        CancellationToken cancellationToken)
    {
        var hasMoreWork = false;
        DateTime? earliestUpcoming = null;
        var pendingDepth = 0L;

        foreach (var target in targets)
        {
            try
            {
                var result = await drainSource(target, cancellationToken).ConfigureAwait(false);
                pendingDepth += result.PendingDepth;
                hasMoreWork |= result.HasMoreWork;
                if (result.EarliestUpcoming is { } upcoming
                    && (earliestUpcoming is null || upcoming < earliestUpcoming))
                {
                    earliestUpcoming = upcoming;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable database must not starve the other sources. A failing source
                // contributes nothing to the wait: its rows are retried on the next signal or interval.
                onSourceError(target.ToString(), ex);
            }
        }

        return (hasMoreWork, earliestUpcoming, pendingDepth);
    }

    /// <summary>
    /// Exponential backoff for a failed row: <c>base * 2^(attempts - 1)</c>, multiplied by a random
    /// jitter factor in <c>[0.8, 1.2]</c> and then capped at <paramref name="capSeconds"/>. The jitter is
    /// what keeps a batch that failed together (one dependency outage fails every row in the same
    /// instant) from retrying in lockstep and re-hammering that dependency on a single shared schedule.
    /// </summary>
    /// <param name="attempts">The attempt number that just failed, counting from one.</param>
    /// <param name="baseSeconds">The configured backoff base, in seconds.</param>
    /// <param name="capSeconds">The ceiling, in seconds (each processor chooses its own).</param>
    /// <returns>The wait, in seconds, before the row becomes claimable again.</returns>
    internal static double ComputeRetryBackoffSeconds(int attempts, int baseSeconds, int capSeconds)
    {
        // Clamp the exponent before it reaches Math.Pow: the attempt limits are bounded at 20 today, but
        // the cap below is what actually decides the wait, so there is no reason to let a future
        // settings change turn this into an overflow.
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 16);
        var backoff = baseSeconds * Math.Pow(2, exponent);

        // Jitter is applied BEFORE the cap so a capped backoff stays exactly at the ceiling.
#pragma warning disable S2245, CA5394 // Random spaces retry attempts apart (jitter); it feeds no security, token, key or cryptographic decision, so a pseudorandom generator is the correct tool here.
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
#pragma warning restore S2245, CA5394

        return Math.Min(backoff * jitter, capSeconds);
    }
}
