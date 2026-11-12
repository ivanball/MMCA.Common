namespace MMCA.Common.Testing.Aspire.Outages;

/// <summary>
/// Runs the clean-up command <see cref="DependencyOutageTestBase{TFixture}"/> issues after a failure
/// while a dependency was down: best effort and bounded, so it can neither replace the original
/// failure nor hang the test.
/// </summary>
internal static class OutageCommands
{
    /// <summary>
    /// Runs <paramref name="command"/> with a token cancelled after <paramref name="budget"/>, stops
    /// waiting for it when the budget runs out even if it ignores that token, and swallows whatever it
    /// throws. It never throws itself (bar an argument error), so a caller's <see langword="finally"/>
    /// lets the original exception propagate unchanged.
    /// </summary>
    /// <param name="command">The command to run.</param>
    /// <param name="budget">How long the command may take.</param>
    /// <returns>A task that completes when the command finished, failed or ran out of budget.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budget"/> is zero or negative.</exception>
    internal static async Task RunBestEffortAsync(Func<CancellationToken, Task> command, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);

        using var bounded = new CancellationTokenSource(budget);
        try
        {
            await command(bounded.Token).WaitAsync(budget, TimeProvider.System).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort: the exception that brought the caller here propagates, not this one.
        }
        finally
        {
            // The wait can end a moment before the budget token fires. Cancel it here so a command
            // that was abandoned at the budget is always told to stop, and never outlives this call
            // holding a token that is disposed without being cancelled. Best effort too: a callback the
            // command registered on the token runs here, and its exception must not escape either.
            try
            {
                await bounded.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Swallowed for the reason the command's own exception is.
            }
        }
    }
}
