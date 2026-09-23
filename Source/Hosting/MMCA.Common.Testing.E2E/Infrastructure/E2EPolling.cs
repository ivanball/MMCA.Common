using System.Diagnostics;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Deadline-bounded polling for end-to-end assertions on eventually-consistent state: a claim that
/// appears once a cross-service event lands, a count that rolls over when a cache expires. A fixed
/// pre-assert sleep is both slow and flaky (too short and the suite reds intermittently, too long and
/// every green run pays the worst case); polling returns as soon as the condition holds and bounds
/// the wait by a deadline rather than an attempt count, so a slow probe cannot stretch it.
/// </summary>
/// <remarks>
/// The E2E package does not reference <c>MMCA.Common.Testing</c>, whose <c>TestPolling</c> serves the
/// integration tiers; this is its counterpart here, with the same shape. Only for "until present"
/// waits: a state that must stay ABSENT is a negative, which a poll that stops on the first success
/// cannot express.
/// </remarks>
public static class E2EPolling
{
    /// <summary>The wait budget when the caller names none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The delay between probes when the caller names none.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Polls <paramref name="probe"/> until <paramref name="isSatisfied"/> holds or the deadline passes,
    /// returning the last probed value either way, so the caller still asserts on it (a timeout must
    /// fail on the real assertion message, not on a bare timeout).
    /// </summary>
    /// <typeparam name="T">The probed value type.</typeparam>
    /// <param name="probe">Reads the current value (a page reload and locator read, an HTTP call).</param>
    /// <param name="isSatisfied">The condition that ends the poll.</param>
    /// <param name="timeout">Total wait budget. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="interval">Delay between probes. Defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="cancellationToken">Cancels the wait between probes.</param>
    /// <returns>The last probed value.</returns>
    public static async Task<T> PollUntilAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> isSatisfied,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(isSatisfied);

        var budget = timeout ?? DefaultTimeout;
        var delay = interval ?? DefaultInterval;
        var started = Stopwatch.GetTimestamp();

        var last = await probe().ConfigureAwait(false);
        while (!isSatisfied(last) && Stopwatch.GetElapsedTime(started) + delay < budget)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            last = await probe().ConfigureAwait(false);
        }

        return last;
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it reports <see langword="true"/> or the deadline passes.
    /// </summary>
    /// <param name="probe">Reports whether the awaited state is present yet.</param>
    /// <param name="timeout">Total wait budget. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="interval">Delay between probes. Defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="cancellationToken">Cancels the wait between probes.</param>
    /// <returns><see langword="true"/> when the probe succeeded within the budget.</returns>
    public static Task<bool> PollUntilAsync(
        Func<Task<bool>> probe,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default) =>
        PollUntilAsync(probe, static present => present, timeout, interval, cancellationToken);
}
