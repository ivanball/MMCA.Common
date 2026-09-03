namespace MMCA.Common.Testing.Support;

/// <summary>
/// Poll-with-timeout helper for asynchronous integration assertions. Anything that travels the outbox to a
/// broker and back (or any other eventually-consistent path) arrives at a time the test cannot know, and a
/// fixed pre-assert sleep is both slow and flaky: too short and the suite reds intermittently, too long and
/// every green run pays the worst case. Polling returns as soon as the condition holds and bounds the wait.
/// </summary>
public static class TestPolling
{
    /// <summary>
    /// Polls <paramref name="probe"/> until <paramref name="isSatisfied"/> holds or the timeout elapses,
    /// returning the last probed value either way, so the caller still asserts on it (a timeout must fail
    /// on the real assertion message, not on a bare timeout).
    /// </summary>
    /// <typeparam name="T">The probed value type.</typeparam>
    /// <param name="probe">Reads the current value (an HTTP call, a repository read, a counter).</param>
    /// <param name="isSatisfied">The condition that ends the poll.</param>
    /// <param name="timeout">Total wait budget. Defaults to 60 seconds.</param>
    /// <param name="interval">Delay between probes. Defaults to 500 milliseconds.</param>
    /// <returns>The last probed value.</returns>
    public static async Task<T> PollUntilAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> isSatisfied,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(isSatisfied);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        var delay = interval ?? TimeSpan.FromMilliseconds(500);
        var last = await probe().ConfigureAwait(false);
        while (!isSatisfied(last) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            last = await probe().ConfigureAwait(false);
        }

        return last;
    }
}
