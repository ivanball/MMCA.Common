namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// Lets the scheduler wake the <see cref="InternalCommandProcessor"/> as soon as a due row has been
/// saved, instead of leaving it to the next polling cycle. The outbox's <c>IOutboxSignal</c> with a
/// separate instance, so a burst of scheduled commands does not consume the outbox's one pending
/// wake-up and vice versa.
/// </summary>
public interface IInternalCommandSignal
{
    /// <summary>Signals that a due internal command is available for execution.</summary>
    void Signal();

    /// <summary>
    /// Waits for a signal or until the timeout elapses, whichever comes first. Used by the
    /// <see cref="InternalCommandProcessor"/> in place of a plain polling delay.
    /// </summary>
    /// <param name="timeout">Maximum time to wait before returning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when signaled or timed out.</returns>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
