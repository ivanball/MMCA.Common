namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Allows the domain event interceptor to signal the <see cref="OutboxProcessor"/>
/// when new outbox entries have been persisted, so it wakes up immediately rather
/// than waiting for the next polling cycle.
/// </summary>
public interface IOutboxSignal
{
    /// <summary>Signals that new outbox entries are available for processing.</summary>
    void Signal();

    /// <summary>
    /// Waits for a signal or until the timeout elapses, whichever comes first.
    /// Used by the <see cref="OutboxProcessor"/> as a replacement for polling delays.
    /// </summary>
    /// <param name="timeout">Maximum time to wait before returning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when signaled or timed out.</returns>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
